using Dispatch.SharedKernel;

namespace Dispatch.WorkManagement.Application.Tests;

/// <summary>A clock the test drives.</summary>
internal sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// An in-memory bus that records everything published and routes it to registered handlers.
/// </summary>
/// <remarks>
/// <para>
/// Stands in for <c>InProcessIntegrationEventPublisher</c>, which lives in the host and uses
/// reflection over the DI container. This does the same job with an explicit handler list, so a
/// test can assert on what was published <em>and</em> let the real handlers run.
/// </para>
/// <para>
/// Dispatch is synchronous and depth-first: publishing an event runs its handlers immediately,
/// and anything they publish runs before the original call returns. That makes a whole saga
/// deterministic inside one <c>await</c>, which is the only reason these tests can assert on an
/// end state without polling or sleeping.
/// </para>
/// <para>
/// The real transport is not synchronous, and the tests are careful not to depend on ordering
/// that a broker would not guarantee. What they assert is that the right events were published
/// and the right end state was reached.
/// </para>
/// </remarks>
internal sealed class TestBus : IIntegrationEventPublisher
{
    private readonly Dictionary<Type, List<Func<IIntegrationEvent, Task>>> _handlers = [];

    public List<IIntegrationEvent> Published { get; } = [];

    public void Subscribe<TEvent>(IIntegrationEventHandler<TEvent> handler)
        where TEvent : IIntegrationEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var list))
        {
            _handlers[typeof(TEvent)] = list = [];
        }

        list.Add(e => handler.HandleAsync((TEvent)e));
    }

    public async Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        Published.Add(integrationEvent);

        if (_handlers.TryGetValue(integrationEvent.GetType(), out var handlers))
        {
            foreach (var handler in handlers)
            {
                await handler(integrationEvent);
            }
        }
    }

    public IEnumerable<TEvent> OfType<TEvent>() where TEvent : IIntegrationEvent =>
        Published.OfType<TEvent>();

    /// <summary>Re-delivers something already published, to test idempotency.</summary>
    /// <remarks>
    /// At-least-once delivery is a property of every real transport, so "what happens on the
    /// second delivery" is a question every handler has to answer. This is how the tests ask it.
    /// </remarks>
    public Task RedeliverAsync(IIntegrationEvent integrationEvent) => PublishAsync(integrationEvent);
}
