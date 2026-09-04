using Dispatch.SharedKernel;

namespace Dispatch.WorkManagement.Domain.WorkOrders;

/// <summary>
/// Every rule this aggregate can refuse on, in one place.
/// </summary>
/// <remarks>
/// Collected here rather than newed up inline at each call site, so that the full set of ways a
/// work order can say no is readable in one screen — and so the API layer can map codes to HTTP
/// statuses exhaustively instead of guessing from message text.
/// </remarks>
public static class WorkOrderErrors
{
    public static readonly Error SummaryRequired =
        new("work_order.summary_required", "A work order needs a summary of the problem.");

    public static Error WrongStatus(string action, WorkOrderStatus actual, params WorkOrderStatus[] expected) =>
        new($"work_order.wrong_status.{action}",
            $"Cannot {action} a work order that is {actual}. Expected: {string.Join(" or ", expected)}.");

    public static readonly Error NotYetOpen =
        new("work_order.window_not_open", "Work cannot start before the scheduled window opens.");

    public static readonly Error NoLabourLogged =
        new("work_order.no_labour", "A work order cannot be completed with no labour logged against it.");

    public static readonly Error AlreadyTerminal =
        new("work_order.terminal", "A completed work order cannot be changed.");

    public static readonly Error CancellationReasonRequired =
        new("work_order.cancellation_reason_required", "Cancelling a work order requires a reason.");
}
