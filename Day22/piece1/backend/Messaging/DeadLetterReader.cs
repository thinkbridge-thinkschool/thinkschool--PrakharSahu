using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;

namespace QuotesApi.Messaging;

public sealed record DeadLetterEntry(
    string MessageId,
    string? Reason,
    string? Description,
    int DeliveryCount,
    DateTimeOffset EnqueuedAt,
    string Body);

public interface IDeadLetterReader
{
    /// <summary>Reads without removing, so inspecting the DLQ does not drain it.</summary>
    Task<IReadOnlyList<DeadLetterEntry>> PeekAsync(
        string subscriptionName, int maxMessages, CancellationToken cancellationToken);

    /// <summary>
    /// Receives and completes dead-lettered messages, permanently discarding them.
    /// </summary>
    /// <remarks>
    /// Exists so verification runs start from a clean slate. In a real system the equivalent
    /// operation is a deliberate one — you fix the cause, then replay or drop, and either way
    /// somebody decides.
    /// </remarks>
    Task<int> PurgeAsync(string subscriptionName, CancellationToken cancellationToken);
}

/// <summary>
/// Stands in when no broker is configured.
/// </summary>
/// <remarks>
/// <para>
/// Registered so that <see cref="IDeadLetterReader"/> is <b>always</b> resolvable. Leaving it
/// unregistered when messaging is off does not merely disable the DLQ endpoints — it stops
/// the entire application from starting, and the error says nothing about messaging:
/// </para>
/// <code>
/// Body was inferred but the method does not allow inferred body parameters.
///   subscription | Route (Inferred)
///   reader       | Body  (Inferred)
/// </code>
/// <para>
/// Minimal APIs decide each parameter's source at startup. An interface the container knows
/// about binds from services; one it does not is assumed to be the request body — and a
/// DELETE may not have an inferred body, so route building throws. The feature flag was
/// meant to make messaging optional, and instead made the API unbootable without a broker.
/// </para>
/// </remarks>
public sealed class DisabledDeadLetterReader : IDeadLetterReader
{
    public Task<IReadOnlyList<DeadLetterEntry>> PeekAsync(
        string subscriptionName, int maxMessages, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeadLetterEntry>>(Array.Empty<DeadLetterEntry>());

    public Task<int> PurgeAsync(string subscriptionName, CancellationToken cancellationToken) =>
        Task.FromResult(0);
}

/// <summary>
/// Reads a subscription's dead-letter queue.
/// </summary>
/// <remarks>
/// <para>
/// The DLQ is not a separate thing you create — every queue and subscription has one, at the
/// sub-path <c>&lt;topic&gt;/Subscriptions/&lt;subscription&gt;/$deadletterqueue</c>. The SDK
/// addresses it with <see cref="SubQueue.DeadLetter"/> rather than by building that path by
/// hand.
/// </para>
/// <para>
/// Each subscription has its own. A message that poisons the audit reader lands in audit's
/// DLQ and nowhere else; if the search indexer handles the same event successfully, its copy
/// is simply completed. That independence is the reason a topic is worth more than a queue
/// here — one bad consumer cannot stall the others.
/// </para>
/// </remarks>
public sealed class ServiceBusDeadLetterReader : IDeadLetterReader
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusOptions _options;

    public ServiceBusDeadLetterReader(ServiceBusClient client, IOptions<ServiceBusOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    private ServiceBusReceiver CreateReceiver(string subscriptionName) =>
        _client.CreateReceiver(_options.TopicName, subscriptionName,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

    public async Task<IReadOnlyList<DeadLetterEntry>> PeekAsync(
        string subscriptionName, int maxMessages, CancellationToken cancellationToken)
    {
        await using var receiver = CreateReceiver(subscriptionName);

        // Peek, not Receive. Receiving would lock the messages and — worse for a diagnostic
        // endpoint — a caller that then failed to settle them would leave them locked for
        // everyone else.
        var messages = await receiver.PeekMessagesAsync(maxMessages, cancellationToken: cancellationToken);

        return messages.Select(m => new DeadLetterEntry(
            m.MessageId,
            m.DeadLetterReason,
            m.DeadLetterErrorDescription,
            m.DeliveryCount,
            m.EnqueuedTime,
            m.Body.ToString())).ToList();
    }

    public async Task<int> PurgeAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        await using var receiver = CreateReceiver(subscriptionName);
        var purged = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // A short wait, not the default. Once the DLQ is empty there is nothing more
            // coming, and the default timeout would stall this loop for a minute to learn it.
            var batch = await receiver.ReceiveMessagesAsync(
                maxMessages: 50, maxWaitTime: TimeSpan.FromSeconds(2), cancellationToken);

            if (batch.Count == 0) break;

            foreach (var message in batch)
            {
                await receiver.CompleteMessageAsync(message, cancellationToken);
                purged++;
            }
        }

        return purged;
    }
}
