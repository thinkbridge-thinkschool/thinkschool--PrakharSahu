using Dispatch.Scheduling.Contracts;
using Dispatch.Scheduling.Domain.Reservations;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Contracts;
using Microsoft.Extensions.Logging;

namespace Dispatch.Scheduling.Application.Reservations;

/// <summary>
/// Why a reservation attempt ended the way it did.
/// </summary>
/// <remarks>
/// A three-state answer rather than a bool, because "somebody else got there first" and "the
/// window was invalid" lead to different conversations with the caller, and because collapsing
/// them would hide the one case that only appears under concurrency.
/// </remarks>
public enum ReservationOutcome
{
    /// <summary>The slot is held.</summary>
    Reserved,

    /// <summary>Another booking already covers part of that window.</summary>
    Conflict
}

/// <summary>How Scheduling stores and queries held slots.</summary>
public interface IReservationRepository
{
    Task<bool> HasOverlapAsync(
        TechnicianId technicianId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);

    /// <summary>
    /// Checks for an overlap and inserts the reservation <b>as one atomic operation</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because <see cref="HasOverlapAsync"/> followed by <see cref="AddAsync"/> is a
    /// check-then-act race: two concurrent requests can both find no overlap and both insert,
    /// and the technician is double-booked. Day 29 recorded that as a known gap; this is the fix.
    /// </para>
    /// <para>
    /// The fix belongs here rather than in the domain. An aggregate is a transactional
    /// consistency boundary, and "is this technician free" is a question about <em>every other
    /// reservation</em> — it cannot be answered from inside one <see cref="Reservation"/> without
    /// loading them all, which is the read-the-world anti-pattern the design rejects. Only the
    /// store can make the check and the write indivisible, so only the store can own it.
    /// </para>
    /// </remarks>
    Task<ReservationOutcome> TryHoldAsync(Reservation reservation, CancellationToken ct = default);

    Task<Reservation?> GetByWorkOrderAsync(Guid workOrderId, CancellationToken ct = default);

    Task AddAsync(Reservation reservation, CancellationToken ct = default);

    /// <summary>
    /// Flushes changes made to a reservation that was loaded from this repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method exists because of a bug the move to a real database exposed.
    /// <c>WorkOrderReleasedHandler</c> loaded a reservation, called <c>Release()</c> on it, and
    /// stopped. Against a dictionary that was correct by accident: the object in the dictionary
    /// <em>was</em> the entity, so mutating it was instantly visible to the next reader.
    /// </para>
    /// <para>
    /// Against EF the change sits in the change tracker and is discarded when the scope ends. The
    /// slot was never released, the technician stayed booked, and nothing failed -- the only
    /// symptom was a rebooking that came back refused. Silent, and caught by the smoke test
    /// rather than by any unit test, because every unit test used the dictionary.
    /// </para>
    /// </remarks>
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>
/// Async flow 1 — WorkManagement says it has scheduled an order; try to hold the slot.
/// </summary>
/// <remarks>
/// <para>
/// The interesting part is that this handler is <b>allowed to say no</b>. WorkManagement asserted
/// intent, not fact: it has no visibility of the technician's calendar and never should have.
/// Scheduling is the only module that can answer "is this person free", and it answers
/// asynchronously, after the work order has already committed.
/// </para>
/// <para>
/// That is a saga, and the price of it is a compensating action —
/// <see cref="TechnicianReservationFailedV1"/>, which WorkManagement handles by walking the order
/// back to Triaged. The alternative would be a distributed transaction across two modules, which
/// is the coupling this whole structure exists to avoid.
/// </para>
/// </remarks>
public sealed class WorkOrderScheduledHandler(
    IReservationRepository reservations,
    IIntegrationEventPublisher publisher,
    ILogger<WorkOrderScheduledHandler> logger)
    : IIntegrationEventHandler<WorkOrderScheduledV1>
{
    public async Task HandleAsync(WorkOrderScheduledV1 e, CancellationToken cancellationToken = default)
    {
        var technicianId = new TechnicianId(e.TechnicianId);

        // Idempotency, and the reason it is a lookup rather than a flag: at-least-once delivery
        // means this event will arrive twice sooner or later. Finding the existing reservation is
        // both the duplicate check and the answer.
        var existing = await reservations.GetByWorkOrderAsync(e.WorkOrderId, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation(
                "Work order {WorkOrderId} already has a reservation. Ignoring duplicate delivery of {EventId}.",
                e.WorkOrderId, e.EventId);
            return;
        }

        var reservation = Reservation.Hold(e.WorkOrderId, technicianId, e.WindowStart, e.WindowEnd);
        if (reservation.IsFailure)
        {
            await publisher.PublishAsync(
                new TechnicianReservationFailedV1(e.WorkOrderId, e.TechnicianId, reservation.Error.Message),
                cancellationToken);
            return;
        }

        // -----------------------------------------------------------------------------------
        // One call, not a check followed by a write.
        //
        // The previous shape was `if (!HasOverlap) Add(...)`, which reads correctly and is wrong
        // the moment two requests arrive together: both queries run before either insert, both
        // see a free calendar, and the technician is booked twice. Nothing fails, nothing logs,
        // and the first anybody knows is two vans at one address.
        //
        // The window between the check and the write cannot be closed in C#. It is closed by the
        // store making them one operation -- see EfReservationRepository.TryHoldAsync.
        // -----------------------------------------------------------------------------------
        var outcome = await reservations.TryHoldAsync(reservation.Value, cancellationToken);

        if (outcome == ReservationOutcome.Conflict)
        {
            // The same event the pre-emptive check used to publish, so the compensating saga in
            // WorkManagement is unchanged: it returns the order to Triaged either way.
            logger.LogInformation(
                "Reservation for work order {WorkOrderId} lost the race for technician {TechnicianId}.",
                e.WorkOrderId, e.TechnicianId);

            await publisher.PublishAsync(
                new TechnicianReservationFailedV1(
                    e.WorkOrderId, e.TechnicianId, "the technician is already booked for that window"),
                cancellationToken);
            return;
        }

        await publisher.PublishAsync(
            new TechnicianReservedV1(e.WorkOrderId, e.TechnicianId, e.WindowStart, e.WindowEnd),
            cancellationToken);
    }
}

/// <summary>
/// The work order is off. Give the slot back.
/// </summary>
/// <remarks>
/// Subscribes to one event covering both cancellation and return-to-triage, because from here
/// they are the same instruction. Releasing is idempotent, so a duplicate delivery costs nothing.
/// </remarks>
public sealed class WorkOrderReleasedHandler(
    IReservationRepository reservations,
    ILogger<WorkOrderReleasedHandler> logger)
    : IIntegrationEventHandler<WorkOrderReleasedV1>
{
    public async Task HandleAsync(WorkOrderReleasedV1 e, CancellationToken cancellationToken = default)
    {
        var reservation = await reservations.GetByWorkOrderAsync(e.WorkOrderId, cancellationToken);

        if (reservation is null)
        {
            // Entirely normal: the order was released before Scheduling ever managed to hold a
            // slot, or this is the second delivery. Nothing to do, and nothing to worry about.
            return;
        }

        reservation.Release();
        await reservations.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Released the slot for work order {WorkOrderId}: {Reason}.", e.WorkOrderId, e.Reason);
    }
}
