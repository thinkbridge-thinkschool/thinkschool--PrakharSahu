using Dispatch.Scheduling.Contracts;
using Dispatch.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Dispatch.WorkManagement.Application.WorkOrders;

/// <summary>
/// Scheduling could not honour a booking. Walk the work order back.
/// </summary>
/// <remarks>
/// <para>
/// One of only three cross-module edges in the solution, and it reaches
/// <c>Dispatch.Scheduling.Contracts</c> — never <c>Dispatch.Scheduling.Domain</c>. The
/// architecture tests fail the build if that ever changes.
/// </para>
/// <para>
/// This is the compensating action of a saga that has no shared transaction. WorkManagement
/// committed "scheduled" optimistically; Scheduling has now said no; this restores the invariant
/// that a scheduled order has a technician who is actually booked.
/// </para>
/// </remarks>
public sealed class ReservationFailedHandler(
    WorkOrderService workOrders,
    ILogger<ReservationFailedHandler> logger)
    : IIntegrationEventHandler<TechnicianReservationFailedV1>
{
    public async Task HandleAsync(TechnicianReservationFailedV1 e, CancellationToken cancellationToken = default)
    {
        var result = await workOrders.ReturnToTriageAsync(
            e.WorkOrderId, $"scheduling refused the booking: {e.Reason}", cancellationToken);

        if (result.IsFailure)
        {
            // Logged, not thrown. A failure here is almost always benign and self-correcting:
            // the order was cancelled, or a duplicate delivery arrived and the first one already
            // did the work. Throwing would tell the transport to redeliver an event that will
            // fail identically forever.
            //
            // That makes this handler idempotent by construction -- the second delivery finds the
            // order already in Triaged and the aggregate refuses the transition, which is exactly
            // the desired no-op.
            logger.LogInformation(
                "Work order {WorkOrderId} was not returned to triage ({Code}). "
                + "Expected when the order was already cancelled or the event was redelivered.",
                e.WorkOrderId, result.Error.Code);
        }
    }
}
