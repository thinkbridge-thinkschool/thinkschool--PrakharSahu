namespace Dispatch.SharedKernel;

/// <summary>
/// Something that happened inside one module, expressed in that module's own language.
/// </summary>
/// <remarks>
/// <para>
/// Domain events are <b>internal</b>. They are free to name the module's own types, they change
/// whenever the module changes, and nothing outside the module ever sees one. That freedom is the
/// point: a module cannot refactor its own model if the shape of its internal events is a public
/// contract.
/// </para>
/// <para>
/// Handled in-process, in the same transaction or immediately after it. Contrast with
/// <see cref="IIntegrationEvent"/>.
/// </para>
/// </remarks>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Something that happened, published for <b>other modules</b> to react to.
/// </summary>
/// <remarks>
/// <para>
/// This is a published contract, and it lives in the module's <c>Contracts</c> project because
/// that is the only thing other modules are allowed to reference. Changing one is a breaking
/// change to every subscriber, so integration events carry primitives and ids — never the
/// module's own entities or value objects, which would drag the internal model across the
/// boundary and make it impossible to change.
/// </para>
/// <para>
/// The rule of thumb this scaffold follows: <b>domain events are how a module talks to itself;
/// integration events are how it talks to everyone else.</b> One is translated into the other at
/// the module edge, on purpose, because that translation is where the coupling stops.
/// </para>
/// </remarks>
public interface IIntegrationEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
}

/// <summary>Convenience base so events do not each re-declare identity and timestamp.</summary>
/// <remarks>
/// <see cref="EventId"/> is the idempotency key a consumer dedupes on. At-least-once delivery
/// means every subscriber will eventually see the same event twice; without a stable id per
/// event there is nothing to recognise the duplicate by.
/// </remarks>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

public abstract record IntegrationEvent : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Wall-clock time, injected rather than read from <c>DateTimeOffset.UtcNow</c>.
/// </summary>
/// <remarks>
/// The domain here is full of time-dependent rules — SLA due dates, scheduled windows, "you
/// cannot start before the window opens". Every one of those is untestable if the aggregate
/// reads the clock itself, because the test would have to wait for real time to pass.
/// </remarks>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
