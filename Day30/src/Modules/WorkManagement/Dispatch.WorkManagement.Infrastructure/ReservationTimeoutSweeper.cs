using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Application.Abstractions;
using Dispatch.WorkManagement.Application.WorkOrders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dispatch.WorkManagement.Infrastructure;

/// <summary>
/// Reacts to a reply that never came.
/// </summary>
/// <remarks>
/// <para>
/// The scheduling saga has two documented outcomes: Scheduling confirms with
/// <c>TechnicianReservedV1</c>, or refuses with <c>TechnicianReservationFailedV1</c>. Nothing
/// handled the third: <b>neither</b>. A dropped event, a poisoned message, or a consumer that
/// crashed mid-handler left the order in <c>Scheduled</c> for ever — having already told a
/// customer that somebody was coming.
/// </para>
/// <para>
/// <b>Why <see cref="SlaSweeper"/> does not already cover this.</b> That one watches DUE DATES.
/// A Low-priority order has ten days of SLA, so a booking that silently failed would surface a
/// week and a half later as a breach rather than immediately as a stuck saga. Two different
/// questions, two different deadlines, two sweepers.
/// </para>
/// <para>
/// <b>This one acts; the SLA sweeper only reports.</b> The difference is that there is an obvious
/// correct action here and it already exists: <c>ReturnToTriage</c>, the same compensating
/// operation an explicit refusal triggers. An SLA breach has no such single answer — it might
/// need reassignment, escalation or a phone call — so reporting is the honest limit there.
/// </para>
/// <para>
/// Every minute, like the SLA sweep. Sooner would mean polling a database for a condition that
/// takes two minutes to become true.
/// </para>
/// </remarks>
public sealed class ReservationTimeoutSweeper(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<ReservationTimeoutSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Reservation timeout sweeper started. Interval {Interval}, grace {Grace}.",
            Interval, Domain.WorkOrders.WorkOrder.ReservationGrace);

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Swallowed on purpose, same reasoning as the SLA sweeper: an unhandled exception
                // in a BackgroundService takes the whole host down by default, and a transient
                // read failure is not a reason to stop serving HTTP traffic.
                logger.LogError(ex, "Reservation timeout sweep failed. The next sweep will retry.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkOrderRepository>();
        var service = scope.ServiceProvider.GetRequiredService<WorkOrderService>();

        var stuck = await repository.GetAwaitingReservationAsync(clock.UtcNow, cancellationToken);

        if (stuck.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "{Count} work order(s) have been waiting for a reservation past their deadline.",
            stuck.Count);

        foreach (var order in stuck)
        {
            // The same compensating operation an explicit refusal triggers, routed through the
            // same use case, with a reason that says which of the two happened. A dispatcher
            // reading the audit trail should be able to tell "Scheduling said no" from
            // "Scheduling said nothing" — they point at different problems, one operational.
            //
            // Going through WorkOrderService rather than mutating and saving here is what gets
            // the domain events dispatched: ReturnToTriage raises WorkOrderReturnedToTriage, and
            // Scheduling releases any slot it did manage to hold in response. A hand-rolled save
            // would have skipped that and left the technician booked.
            var result = await service.ReturnToTriageAsync(
                order.Id.Value, "scheduling did not confirm the booking in time", cancellationToken);

            if (result.IsFailure)
            {
                // Benign and expected: the confirmation or refusal landed between the query and
                // this line, so the order has already moved on. The next sweep will not see it.
                logger.LogInformation(
                    "Work order {WorkOrderId} moved on before the timeout could act ({Code}).",
                    order.Id, result.Error.Code);
                continue;
            }

            logger.LogWarning(
                "Work order {WorkOrderId} returned to triage: no reservation reply within {Grace}.",
                order.Id, Domain.WorkOrders.WorkOrder.ReservationGrace);
        }
    }
}
