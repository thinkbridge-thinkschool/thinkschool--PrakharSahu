namespace Dispatch.SharedKernel;

/// <summary>
/// Publishes an integration event to whoever is listening.
/// </summary>
/// <remarks>
/// <para>
/// The seam that lets this stay a monolith today and stop being one later. Right now the only
/// implementation dispatches in-process; swapping it for a Service Bus topic is a change to one
/// class in the composition root, because no module has ever been allowed to know which it was
/// talking to.
/// </para>
/// <para>
/// That is the practical argument for a modular monolith over microservices at this stage: the
/// boundaries are real and enforced, but they are still <em>cheap to move</em>. Boundaries drawn
/// in the first week are usually wrong, and redrawing one here is a refactor rather than a
/// migration.
/// </para>
/// </remarks>
public interface IIntegrationEventPublisher
{
    Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}

/// <summary>Reacts to something another module published.</summary>
/// <remarks>
/// <para>
/// Handlers must be <b>idempotent</b>. Delivery is at-least-once whether the transport is an
/// in-process list or a broker, so every handler will eventually be handed the same event twice —
/// on a retry, on a redeploy mid-dispatch, on a duplicate publish. Dedupe on
/// <see cref="IIntegrationEvent.EventId"/>.
/// </para>
/// <para>
/// A handler must also never throw to signal a business decision. "I chose not to act on this"
/// is a normal outcome and belongs in a log line; an exception tells the transport to redeliver,
/// which turns a decision into an infinite loop.
/// </para>
/// </remarks>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Commits the aggregates changed in this operation, then dispatches what they recorded.
/// </summary>
/// <remarks>
/// The ordering is the contract, and it is the whole reason this interface exists rather than
/// letting handlers save for themselves: <b>persist first, publish second</b>. Publishing before
/// the commit announces a decision the transaction may still roll back, and no amount of
/// subscriber cleverness can un-send the email that followed.
/// </remarks>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
