using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dispatch.WorkManagement.Infrastructure;

/// <summary>
/// Periodically looks for work orders that have blown their SLA deadline.
/// </summary>
/// <remarks>
/// <para>
/// The third async flow, and the one with no trigger. Nothing <em>happens</em> when a deadline
/// passes — that is the whole problem with deadlines. The other two flows react to an event
/// someone caused; this one reacts to the absence of an event, which is why it needs a clock and
/// a loop rather than a subscription.
/// </para>
/// <para>
/// It only reads and reports. Breach is computed by <c>WorkOrder.HasBreachedSla</c> and never
/// stored, because a stored flag is wrong from the moment the deadline passes until the next
/// sweep — and writing to every open order just to keep a boolean honest is a lot of contention
/// to buy a value that a subtraction already gives you.
/// </para>
/// <para>
/// A scope per tick, not per service: this is a singleton and the repository is scoped, so
/// capturing one at construction would hold a single scope open for the lifetime of the process.
/// </para>
/// </remarks>
public sealed class SlaSweeper(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<SlaSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SLA sweeper started, checking every {Interval}.", Interval);

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Caught and swallowed on purpose. An unhandled exception in a BackgroundService
                // takes the whole host down by default, and a transient read failure is not a
                // reason to stop serving HTTP traffic. The loop survives to try again.
                logger.LogError(ex, "SLA sweep failed. Continuing; the next sweep will retry.");
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

        var breaching = await repository.GetBreachingSlaAsync(clock.UtcNow, cancellationToken);

        if (breaching.Count == 0)
        {
            return;
        }

        logger.LogWarning("{Count} work order(s) have breached their SLA.", breaching.Count);

        foreach (var order in breaching)
        {
            logger.LogWarning(
                "Work order {WorkOrderId} ({Priority}) was due {DueBy} and is still {Status}.",
                order.Id, order.Priority, order.DueBy, order.Status);
        }
    }
}
