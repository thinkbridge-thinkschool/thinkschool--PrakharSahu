using Dispatch.Billing.Application.Invoices;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Application.WorkOrders;
using Dispatch.WorkManagement.Domain.WorkOrders;

namespace Dispatch.Api.Endpoints;

public sealed record TriageRequest(WorkOrderPriority Priority);
public sealed record CancelRequest(string Reason);

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
        });

        group.MapGet("/{id:guid}", async (Guid id, WorkOrderService service, CancellationToken ct) =>
        {
            var order = await service.GetAsync(id, ct);
            return order is null ? Results.NotFound() : Results.Ok(ToResponse(order));
        });

        group.MapPost("/{id:guid}/triage", async (Guid id, TriageRequest r, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.TriageAsync(id, r.Priority, ct)));

        group.MapPost("/{id:guid}/schedule", async (Guid id, ScheduleWorkOrderRequest r, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.ScheduleAsync(id, r, ct)));

        group.MapPost("/{id:guid}/start", async (Guid id, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.StartAsync(id, ct)));

        group.MapPost("/{id:guid}/labour", async (Guid id, LogLabourRequest r, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.LogLabourAsync(id, r, ct)));

        group.MapPost("/{id:guid}/complete", async (Guid id, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.CompleteAsync(id, ct)));

        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelRequest r, WorkOrderService s, CancellationToken ct) =>
            Respond(await s.CancelAsync(id, r.Reason, ct)));
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
    private static IResult Problem(Error error) => error.Code switch
    {
        "work_order.not_found" => Results.NotFound(new { error.Code, error.Message }),
        var code when code.StartsWith("work_order.wrong_status", StringComparison.Ordinal)
            => Results.Conflict(new { error.Code, error.Message }),
        "work_order.terminal" => Results.Conflict(new { error.Code, error.Message }),
        _ => Results.BadRequest(new { error.Code, error.Message })
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
        });
    }
}
