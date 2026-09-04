using Dispatch.Scheduling.Contracts;
using Dispatch.Scheduling.Domain.Reservations;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Contracts;
using Microsoft.Extensions.Logging;

namespace Dispatch.Scheduling.Application.Reservations;

/// <summary>How Scheduling stores and queries held slots.</summary>
public interface IReservationRepository
{
    Task<bool> HasOverlapAsync(
        TechnicianId technicianId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);

    Task<Reservation?> GetByWorkOrderAsync(Guid workOrderId, CancellationToken ct = default);

    Task AddAsync(Reservation reservation, CancellationToken ct = default);
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

        if (await reservations.HasOverlapAsync(technicianId, e.WindowStart, e.WindowEnd, cancellationToken))
        {
            await publisher.PublishAsync(
                new TechnicianReservationFailedV1(
                    e.WorkOrderId, e.TechnicianId, "the technician is already booked for that window"),
                cancellationToken);
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

        await reservations.AddAsync(reservation.Value, cancellationToken);

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
        logger.LogInformation("Released the slot for work order {WorkOrderId}: {Reason}.", e.WorkOrderId, e.Reason);
    }
}
