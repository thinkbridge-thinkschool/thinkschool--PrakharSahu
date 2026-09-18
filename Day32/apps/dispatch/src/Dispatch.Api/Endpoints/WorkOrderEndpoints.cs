using Dispatch.Billing.Application.Invoices;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Application.WorkOrders;
using Dispatch.WorkManagement.Domain.WorkOrders;

using Dispatch.Api.Security;

namespace Dispatch.Api.Endpoints;

public sealed record TriageRequest(WorkOrderPriority Priority);
public sealed record CancelRequest(string Reason);

/// <remarks>
/// Every endpoint carries a policy, and which policy is a domain statement rather than a security
/// one. A technician may start, log labour against, and complete work. Only a dispatcher may raise,
/// triage, schedule or cancel it — because labour hours become an invoice, and the principal that
/// records the hours should not also be the one that committed the customer to the visit.
/// </remarks>
public static class WorkOrderEndpoints
{
    public static void MapWorkOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/work-orders");

        group.MapPost("/", async (RaiseWorkOrderRequest request, WorkOrderService service, CancellationToken ct) =>
        {
            var result = await service.RaiseAsync(request, ct);
            return result.IsSuccess
                ? Results.Created($"/api/work-orders/{result.Value}", new { id = result.Value })
                : Problem(result.Error);
        }).RequireAuthorization(DispatchAuth.DispatcherPolicy);

        group.MapGet("/{id:guid}", async (Guid id, WorkOrderService service, CancellationToken ct) =>
        {
            var order = await service.GetAsync(id, ct);
            return order is null ? Results.NotFound() : Results.Ok(ToResponse(order));
        }).RequireAuthorization(DispatchAuth.ReadPolicy);

        group.MapPost("/{id:guid}/triage", async (Guid id, TriageRequest r, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.TriageAsync(id, r.Priority, ct)))
            .RequireAuthorization(DispatchAuth.DispatcherPolicy);

        group.MapPost("/{id:guid}/schedule", async (Guid id, ScheduleWorkOrderRequest r, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.ScheduleAsync(id, r, ct)))
            .RequireAuthorization(DispatchAuth.DispatcherPolicy);

        group.MapPost("/{id:guid}/start", async (Guid id, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.StartAsync(id, ct)))
            .RequireAuthorization(DispatchAuth.TechnicianPolicy);

        group.MapPost("/{id:guid}/labour", async (Guid id, LogLabourRequest r, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.LogLabourAsync(id, r, ct)))
            .RequireAuthorization(DispatchAuth.TechnicianPolicy);

        group.MapPost("/{id:guid}/complete", async (Guid id, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.CompleteAsync(id, ct)))
            .RequireAuthorization(DispatchAuth.TechnicianPolicy);

        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelRequest r, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.CancelAsync(id, r.Reason, ct)))
            .RequireAuthorization(DispatchAuth.DispatcherPolicy);
    }

    /// <summary>
    /// Projection, not the aggregate.
    /// </summary>
    /// <remarks>
    /// Serialising <see cref="WorkOrder"/> directly would make every private field a public API
    /// contract by accident, and the first internal rename would be a breaking change for
    /// clients. It also leaks the module's own vocabulary out over HTTP, which is the same
    /// mistake the Contracts project exists to prevent between modules.
    /// </remarks>
    private static object ToResponse(WorkOrder order) => new
    {
        id = order.Id.Value,
        status = order.Status.ToString(),
        summary = order.Summary,
        address = order.Address.ToString(),
        priority = order.Priority?.ToString(),
        dueBy = order.DueBy,
        technicianId = order.AssignedTechnicianId?.Value,
        window = order.Window is null ? null : new { start = order.Window.Start, end = order.Window.End },
        totalLabourMinutes = order.TotalLabourMinutes,
        isBillable = order.IsBillable
    };

    private static IResult Respond(Result result) =>
        result.IsSuccess ? Results.NoContent() : Problem(result.Error);

    /// <summary>
    /// Maps a domain error code onto an HTTP status.
    /// </summary>
    /// <remarks>
    /// Keyed on <see cref="Error.Code"/>, never on message text. This is the payoff for having
    /// codes at all: the mapping is stable while the wording stays free to change.
    ///
    /// 409 rather than 400 for a rejected transition, because the request was well-formed — the
    /// resource was simply not in a state that allows it. A client that retries a 400 is
    /// confused; a client that retries a 409 after refreshing is behaving correctly.
    /// </remarks>
    /// <summary>
    /// Maps a domain error to a status code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The distinction being drawn is 400 versus 409, and it is about whose problem it is.</b>
    /// A 400 tells the caller their request is malformed: fix the payload and try again. A 409
    /// says the request was fine and the resource is not in a state that permits it: do something
    /// else first, then retry the same request unchanged.
    /// </para>
    /// <para>
    /// Getting that wrong sends a client hunting for a bug in a request that has nothing wrong
    /// with it. <c>no_labour</c> and <c>window_not_open</c> used to fall through to 400 by
    /// default — "you cannot complete an order with no labour logged" is not a malformed request,
    /// it is an order that needs labour logged first. The integration tests caught both, because
    /// no unit test sees a status code.
    /// </para>
    /// <para>
    /// Every code is now listed explicitly and the default <b>throws</b>. A new domain error must
    /// be classified deliberately rather than silently becoming a 400 — which is how these two
    /// got mislabelled in the first place.
    /// </para>
    /// </remarks>
    private static IResult Problem(Error error) => error.Code switch
    {
        // Gone.
        "work_order.not_found" => Results.NotFound(new { error.Code, error.Message }),

        // The resource's state forbids this. The request is fine.
        var code when code.StartsWith("work_order.wrong_status", StringComparison.Ordinal)
            => Results.Conflict(new { error.Code, error.Message }),
        "work_order.terminal" => Results.Conflict(new { error.Code, error.Message }),
        "work_order.no_labour" => Results.Conflict(new { error.Code, error.Message }),
        "work_order.window_not_open" => Results.Conflict(new { error.Code, error.Message }),

        // The request itself is wrong: a missing field, or a value that cannot be accepted.
        "work_order.summary_required" => Results.BadRequest(new { error.Code, error.Message }),
        "work_order.summary_too_long" => Results.BadRequest(new { error.Code, error.Message }),
        // Found by the Day 31 security audit: a 5 MB body returned 500 because these two codes
        // were never classified, so the deliberately-fatal default fired. That is the mechanism
        // working -- an unmapped error was loud instead of silently becoming a plausible 400.
        "address.incomplete" => Results.BadRequest(new { error.Code, error.Message }),
        "address.line_too_long" => Results.BadRequest(new { error.Code, error.Message }),
        "work_order.cancellation_reason_required" => Results.BadRequest(new { error.Code, error.Message }),
        "window.in_the_past" => Results.BadRequest(new { error.Code, error.Message }),
        "window.inverted" => Results.BadRequest(new { error.Code, error.Message }),
        "labour.not_positive" => Results.BadRequest(new { error.Code, error.Message }),
        "labour.implausible" => Results.BadRequest(new { error.Code, error.Message }),

        // Deliberately fatal. An unclassified error reaching a caller as a plausible-looking 400
        // is the failure this mapping exists to prevent; a 500 in a test run is louder and
        // cheaper than a wrong status code in production.
        _ => throw new InvalidOperationException(
            $"Unmapped domain error '{error.Code}'. Classify it in WorkOrderEndpoints.Problem: "
            + "409 if the resource's state forbids the operation, 400 if the request is invalid.")
    };
}

public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this WebApplication app)
    {
        // Read-only, and there deliberately is no "create invoice" route. Invoices are not
        // something a user asks for; they are a consequence of work being completed, and the only
        // way one comes into existence is the WorkOrderCompletedV1 subscription.
        app.MapGet("/api/invoices", async (IInvoiceRepository invoices, CancellationToken ct) =>
        {
            var all = await invoices.ListAsync(ct);
            return Results.Ok(all.Select(i => new
            {
                id = i.Id.Value,
                workOrderId = i.WorkOrderId,
                customerId = i.CustomerId,
                total = i.Total.Amount,
                currency = i.Total.Currency,
                isIssued = i.IsIssued
            }));
        }).RequireAuthorization(DispatchAuth.ReadPolicy);
    }
}
