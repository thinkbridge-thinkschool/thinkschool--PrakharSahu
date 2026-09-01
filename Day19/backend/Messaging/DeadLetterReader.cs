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
