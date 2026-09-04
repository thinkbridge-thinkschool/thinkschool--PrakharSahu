using Dispatch.SharedKernel;

namespace Dispatch.Billing.Contracts;

/// <summary>An invoice has been drafted for a completed work order.</summary>
/// <remarks>
/// Nothing in this scaffold subscribes to it yet. It is published anyway because the moment an
/// invoice exists is a genuine business fact, and the alternative -- adding the event later, when
/// something finally needs it -- means retrofitting publication into code that has since grown
/// three more callers.
/// </remarks>
public sealed record InvoiceDraftedV1(
    Guid InvoiceId,
    Guid WorkOrderId,
    Guid CustomerId,
    decimal AmountExcludingTax,
    string Currency) : IntegrationEvent;
