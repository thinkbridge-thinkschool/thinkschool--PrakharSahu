using Dispatch.Billing.Contracts;
using Dispatch.Billing.Domain.Invoices;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Contracts;
using Microsoft.Extensions.Logging;

namespace Dispatch.Billing.Application.Invoices;

public interface IInvoiceRepository
{
    Task<Invoice?> GetByWorkOrderAsync(Guid workOrderId, CancellationToken ct = default);
    Task AddAsync(Invoice invoice, CancellationToken ct = default);
    Task<IReadOnlyList<Invoice>> ListAsync(CancellationToken ct = default);
}

/// <summary>
/// Async flow 2 -- a work order was completed, so draft an invoice for it.
/// </summary>
/// <remarks>
/// <para>
/// The clearest illustration of why these modules talk asynchronously. A field engineer taps
/// "done" on a phone with two bars of signal. If completing the job required Billing to
/// successfully price and store an invoice in the same request, then an accounting problem --
/// or a slow database, or a deployment -- would stop engineers finishing work.
/// </para>
/// <para>
/// Instead the work order commits, the event goes out, and this happens whenever it happens. If
/// Billing is down the event waits. The engineer never finds out, which is correct: whether the
/// customer has been invoiced is not their problem.
/// </para>
/// <para>
/// Everything this needs -- minutes, customer, work order id -- travels on the event, so nothing
/// here calls back into WorkManagement. A synchronous query across a module boundary would
/// reintroduce exactly the coupling the event removed.
/// </para>
/// </remarks>
public sealed class WorkOrderCompletedHandler(
    IInvoiceRepository invoices,
    IIntegrationEventPublisher publisher,
    ILogger<WorkOrderCompletedHandler> logger)
    : IIntegrationEventHandler<WorkOrderCompletedV1>
{
    public async Task HandleAsync(WorkOrderCompletedV1 e, CancellationToken cancellationToken = default)
    {
        // Idempotency. Without this check, a redelivered completion event invoices the customer
        // twice -- the single most expensive kind of duplicate this system can produce.
        var existing = await invoices.GetByWorkOrderAsync(e.WorkOrderId, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation(
                "Work order {WorkOrderId} is already invoiced. Ignoring duplicate delivery of {EventId}.",
                e.WorkOrderId, e.EventId);
            return;
        }

        var invoice = Invoice.Draft(e.WorkOrderId, e.CustomerId, e.TotalLabourMinutes);
        if (invoice.IsFailure)
        {
            // A business decision, logged rather than thrown. Throwing would ask the transport to
            // redeliver an event that is going to fail identically every time.
            logger.LogWarning(
                "No invoice drafted for work order {WorkOrderId}: {Reason}", e.WorkOrderId, invoice.Error.Message);
            return;
        }

        await invoices.AddAsync(invoice.Value, cancellationToken);

        logger.LogInformation(
            "Drafted invoice {InvoiceId} for work order {WorkOrderId}: {Total}.",
            invoice.Value.Id, e.WorkOrderId, invoice.Value.Total);

        await publisher.PublishAsync(
            new InvoiceDraftedV1(
                invoice.Value.Id.Value, e.WorkOrderId, e.CustomerId,
                invoice.Value.Total.Amount, invoice.Value.Total.Currency),
            cancellationToken);
    }
}
