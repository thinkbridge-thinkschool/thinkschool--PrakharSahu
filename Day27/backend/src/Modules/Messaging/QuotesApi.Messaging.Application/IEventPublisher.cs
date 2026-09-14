using QuotesApi.Quotes.Contracts;

namespace QuotesApi.Messaging.Application;

public interface IEventPublisher
{
    /// <summary>
    /// Publishes one event to the topic. Every subscription receives its own copy.
    /// </summary>
    /// <returns>The MessageId that was stamped — the key consumers dedupe on.</returns>
    Task<string> PublishAsync(QuoteEvent @event, CancellationToken cancellationToken);
}
