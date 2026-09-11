using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;

namespace QuotesApi.Messaging;

public interface IEventPublisher
{
    /// <summary>
    /// Publishes one event to the topic. Every subscription receives its own copy.
    /// </summary>
    /// <returns>The MessageId that was stamped — the key consumers dedupe on.</returns>
    Task<string> PublishAsync(QuoteEvent @event, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes to the Service Bus <b>topic</b>, not to a queue.
/// </summary>
/// <remarks>
/// <para>
/// The distinction is the whole reason today exists. A queue delivers each message to one
/// consumer; a topic copies it to every subscription. Here one <c>quote.created</c> event
/// reaches both the audit reader and the search indexer, and neither knows the other exists —
/// adding a third consumer later means adding a subscription, with no change to this class
/// and no redeploy of the publisher.
/// </para>
/// <para>
/// The sender is created once and kept. <see cref="ServiceBusClient"/> owns an AMQP
/// connection, and creating one per publish would open and tear down a connection per HTTP
/// request — the classic mistake that turns a fast broker into a slow one.
/// </para>
/// </remarks>
public sealed class ServiceBusEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusEventPublisher> _logger;

    public ServiceBusEventPublisher(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        ILogger<ServiceBusEventPublisher> logger)
    {
        _sender = client.CreateSender(options.Value.TopicName);
        _logger = logger;
    }

    public async Task<string> PublishAsync(QuoteEvent @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var body = @event.Malformed
            // Deliberately not valid JSON for the consumer's contract. Used to demonstrate
            // the dead-letter path that should NOT be retried.
            ? "{ this is not the payload any consumer expects"
            : JsonSerializer.Serialize(@event);

        var message = new ServiceBusMessage(body)
        {
            // The idempotency key, carried by the transport.
            //
            // Set explicitly from the event rather than left to the SDK, which would generate
            // a fresh Guid per send. If a publish times out and is retried, the broker may
            // already hold the first copy — with a stable MessageId the consumer recognises
            // the second delivery as the same event. With a generated one it processes the
            // work twice and dedupe is decorative.
            MessageId = @event.EventId,
            ContentType = "application/json",
            Subject = @event.EventType,

            // Application properties are readable by subscription filters WITHOUT
            // deserialising the body — which is how a rule can route on event type cheaply.
            ApplicationProperties =
            {
                ["eventType"] = @event.EventType,
                ["quoteId"] = @event.QuoteId,
                ["poison"] = @event.Poison
            }
        };

        await _sender.SendMessageAsync(message, cancellationToken);

        _logger.LogInformation(
            "Published {EventType} for quote {QuoteId} as MessageId {MessageId}.",
            @event.EventType, @event.QuoteId, message.MessageId);

        return message.MessageId;
    }

    public async ValueTask DisposeAsync() => await _sender.DisposeAsync();
}

/// <summary>
/// Used when no connection string is configured.
/// </summary>
/// <remarks>
/// Keeps the API and its tests runnable on a machine with no broker. It logs rather than
/// throwing, because a missing broker should degrade the messaging feature, not break quote
/// creation — the publish is a side effect of the request, not its purpose.
/// </remarks>
public sealed class NoOpEventPublisher : IEventPublisher
{
    private readonly ILogger<NoOpEventPublisher> _logger;

    public NoOpEventPublisher(ILogger<NoOpEventPublisher> logger) => _logger = logger;

    public Task<string> PublishAsync(QuoteEvent @event, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Messaging is disabled; dropped {EventType} for quote {QuoteId}.",
            @event.EventType, @event.QuoteId);

        return Task.FromResult(@event.EventId);
    }
}
