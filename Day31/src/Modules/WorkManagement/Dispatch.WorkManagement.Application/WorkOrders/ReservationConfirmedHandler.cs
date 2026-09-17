using Dispatch.Scheduling.Contracts;
using Dispatch.WorkManagement.Application.Abstractions;
using Dispatch.WorkManagement.Domain.WorkOrders;
using Dispatch.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Dispatch.WorkManagement.Application.WorkOrders;

/// <summary>
/// Scheduling confirmed the booking. Close the saga.
/// </summary>
/// <remarks>
/// <para>
/// <b>This handler did not exist, and its absence was the bug.</b> Scheduling published
/// <c>TechnicianReservedV1</c> on every successful hold and nothing subscribed to it. The failure
/// reply had a handler; the success reply went nowhere.
/// </para>
/// <para>
/// That left a work order in <c>Scheduled</c> looking identical whether Scheduling had confirmed,
/// refused, or never answered at all — so nothing could distinguish a booking that was real from
/// one that was still in flight, and nothing could notice a reply that never came. The timeout
/// sweeper added alongside this handler depends entirely on there being a confirmation to check
/// for.
/// </para>
/// <para>
/// Idempotent, because delivery is at-least-once. The second delivery finds
/// <c>ReservationConfirmedAt</c> already set and returns success without changing anything.
/// </para>
/// </remarks>
public sealed class ReservationConfirmedHandler(
    WorkOrderService workOrders,
    ILogger<ReservationConfirmedHandler> logger)
    : IIntegrationEventHandler<TechnicianReservedV1>
{
    public async Task HandleAsync(TechnicianReservedV1 e, CancellationToken cancellationToken = default)
    {
        var result = await workOrders.ConfirmReservationAsync(e.WorkOrderId, cancellationToken);

        if (result.IsFailure)
        {
            // Reachable when the order was cancelled or returned to triage before the confirmation
            // arrived. Logged at Warning rather than Information, because unlike a duplicate
            // delivery this means Scheduling is holding a slot for an order that no longer wants
            // it -- the release event and this confirmation crossed in flight.
            logger.LogWarning(
                "Could not confirm the reservation for work order {WorkOrderId} ({Code}). "
                + "Scheduling may be holding a slot that nothing will use.",
                e.WorkOrderId, result.Error.Code);
            return;
        }

        logger.LogInformation(
            "Reservation confirmed for work order {WorkOrderId} with technician {TechnicianId}.",
            e.WorkOrderId, e.TechnicianId);
    }
}
