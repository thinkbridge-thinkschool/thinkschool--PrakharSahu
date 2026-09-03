using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using QuotesApi.Messaging.Handlers;

namespace QuotesApi.Messaging;

/// <summary>
/// Drains both subscriptions with competing consumers.
/// </summary>
/// <remarks>
/// <para><b>Competing consumers, concretely.</b> This starts
/// <see cref="ServiceBusOptions.ConsumersPerSubscription"/> independent processors against
/// <em>each</em> subscription, and each processor handles
/// <see cref="ServiceBusOptions.MaxConcurrentCalls"/> messages at once. They all pull from the
/// same subscription and the broker hands each message to exactly one of them — no
/// coordination, no partitioning, no leader election. Throughput scales by adding consumers;
/// correctness does not depend on how many there are.</para>
///
/// <para>In production the consumers would be separate replicas. Running several in one
/// process is what lets a laptop show that competing consumers do not double-process, which
/// is the property people most often assume rather than verify.</para>
///
/// <para><b>Settlement is manual.</b> <c>AutoCompleteMessages = false</c>, deliberately. With
/// the default the SDK completes a message the moment the handler returns and abandons it if
/// the handler throws — which sounds right and quietly removes the ability to distinguish
/// "retry this" from "this will never work, dead-letter it now". Those two decisions are the
/// substance of the dead-letter story, so this class makes them explicitly.</para>
/// </remarks>
public sealed class SubscriptionWorker : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProcessedMessageTracker _tracker;
    private readonly ServiceBusOptions _options;
    private readonly ILogger<SubscriptionWorker> _logger;

    private readonly List<ServiceBusProcessor> _processors = new();

    public SubscriptionWorker(
        ServiceBusClient client,
        IServiceScopeFactory scopeFactory,
        IProcessedMessageTracker tracker,
        IOptions<ServiceBusOptions> options,
        ILogger<SubscriptionWorker> logger)
    {
        _client = client;
        _scopeFactory = scopeFactory;
        _tracker = tracker;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriptions = new[] { _options.AuditSubscription, _options.SearchIndexSubscription };

        foreach (var subscription in subscriptions)
        {
            for (var consumerIndex = 1; consumerIndex <= _options.ConsumersPerSubscription; consumerIndex++)
            {
                var consumerId = $"{subscription}#{consumerIndex}";

                var processor = _client.CreateProcessor(_options.TopicName, subscription,
                    new ServiceBusProcessorOptions
                    {
                        MaxConcurrentCalls = _options.MaxConcurrentCalls,
                        AutoCompleteMessages = false,
                        // Keeps the lock alive while a slow handler runs. Without it a handler
                        // outliving the lock loses the message to another consumer mid-flight,
                        // and the work is done twice — which dedupe then has to catch.
                        MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(2)
                    });

                processor.ProcessMessageAsync += args => OnMessageAsync(subscription, consumerId, args);
                processor.ProcessErrorAsync += args => OnErrorAsync(consumerId, args);

                _processors.Add(processor);
                await processor.StartProcessingAsync(stoppingToken);

                _logger.LogInformation(
                    "Consumer {ConsumerId} started on topic '{Topic}' (concurrency {Concurrency}).",
                    consumerId, _options.TopicName, _options.MaxConcurrentCalls);
            }
        }

        _logger.LogInformation(
            "{Count} competing consumers running across {SubscriptionCount} subscriptions.",
            _processors.Count, subscriptions.Length);

        // Nothing to loop over — the processors own their own receive pumps. This just parks
        // until shutdown, and the Delay is cancelled rather than completing.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task OnMessageAsync(string subscription, string consumerId, ProcessMessageEventArgs args)
    {
        var message = args.Message;
        var messageId = message.MessageId;
        var attempt = message.DeliveryCount;

        // -----------------------------------------------------------------------------
        // 1. Can it be understood at all?
        //
        // A payload this consumer cannot parse will not parse on the next delivery either.
        // Retrying it three times and letting the broker dead-letter it wastes two deliveries
        // and buries the real reason under "MaxDeliveryCountExceeded". Dead-lettering it
        // immediately, with the actual cause, is both faster and more honest.
        // -----------------------------------------------------------------------------
        QuoteEvent? @event;
        try
        {
            @event = JsonSerializer.Deserialize<QuoteEvent>(message.Body.ToString());
            if (@event is null) throw new JsonException("Body deserialised to null.");
        }
        catch (JsonException failure)
        {
            _logger.LogError(
                "[{ConsumerId}] MessageId {MessageId} is unreadable; dead-lettering without retry. {Reason}",
                consumerId, messageId, failure.Message);

            await args.DeadLetterMessageAsync(
                message,
                deadLetterReason: "MalformedPayload",
                deadLetterErrorDescription: failure.Message,
                cancellationToken: args.CancellationToken);
            return;
        }

        // -----------------------------------------------------------------------------
        // 2. Have we already done this, on THIS subscription?
        //
        // Keyed by subscription, not by MessageId alone — the same id arrives on both
        // subscriptions by design, and sharing the key would make each subscription suppress
        // the other's work. Completing (not abandoning) is right: the work is already done,
        // so redelivering achieves nothing.
        // -----------------------------------------------------------------------------
        if (!_tracker.TryBeginProcessing(subscription, messageId))
        {
            _logger.LogInformation(
                "[{ConsumerId}] MessageId {MessageId} already processed; completing without re-running the handler.",
                consumerId, messageId);

            await args.CompleteMessageAsync(message, args.CancellationToken);
            return;
        }

        // -----------------------------------------------------------------------------
        // 3. Do the work.
        // -----------------------------------------------------------------------------
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var handler = scope.ServiceProvider
                .GetServices<ISubscriptionHandler>()
                .FirstOrDefault(h => string.Equals(h.SubscriptionName, subscription, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No handler registered for subscription '{subscription}'.");

            await handler.HandleAsync(@event, args.CancellationToken);
            await args.CompleteMessageAsync(message, args.CancellationToken);

            _logger.LogInformation(
                "[{ConsumerId}] completed MessageId {MessageId} on attempt {Attempt}.",
                consumerId, messageId, attempt);
        }
        catch (Exception failure)
        {
            // The claim has to go back before abandoning, or the redelivery is suppressed as a
            // duplicate and completed without ever running — the message would "succeed"
            // having done nothing, and never reach the dead-letter queue.
            _tracker.Release(subscription, messageId);

            _logger.LogWarning(
                failure,
                "[{ConsumerId}] MessageId {MessageId} failed on attempt {Attempt} of {Max}. Abandoning for redelivery.",
                consumerId, messageId, attempt, _options.MaxDeliveryCount);

            // Abandon, not DeadLetter. The broker counts deliveries and moves the message to
            // the dead-letter queue itself once MaxDeliveryCount is exceeded, stamping
            // DeadLetterReason=MaxDeliveryCountExceeded. Dead-lettering by hand here would
            // rob the message of its remaining retries — and a transient fault deserves them.
            await args.AbandonMessageAsync(message, cancellationToken: args.CancellationToken);
        }
    }

    private Task OnErrorAsync(string consumerId, ProcessErrorEventArgs args)
    {
        // Transport-level problems: a dropped connection, an expired lock, a broker fault.
        // Never handler exceptions — those are caught above and never surface here.
        _logger.LogError(
            args.Exception,
            "[{ConsumerId}] Service Bus error during {Operation} on {Entity}.",
            consumerId, args.ErrorSource, args.EntityPath);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops each processor, waiting for in-flight handlers to settle their messages.
    /// </summary>
    /// <remarks>
    /// <c>StopProcessingAsync</c> stops the receive pump and waits for handlers already
    /// running to finish. Skipping it and disposing straight away drops in-flight messages
    /// mid-handler — they are not lost (the lock expires and they are redelivered), but the
    /// delivery count burns for no reason, and enough restarts will dead-letter a message
    /// that never actually failed.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping {Count} consumers.", _processors.Count);

        foreach (var processor in _processors)
        {
            try
            {
                await processor.StopProcessingAsync(cancellationToken);
                await processor.DisposeAsync();
            }
            catch (Exception failure)
            {
                // One processor failing to stop must not prevent the others from stopping.
                _logger.LogWarning(failure, "A consumer did not stop cleanly.");
            }
        }

        await base.StopAsync(cancellationToken);
    }
}
