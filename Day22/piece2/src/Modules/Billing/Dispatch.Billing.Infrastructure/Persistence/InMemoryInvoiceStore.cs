using System.Collections.Concurrent;
using Dispatch.Billing.Application.Invoices;
using Dispatch.Billing.Domain.Invoices;

namespace Dispatch.Billing.Infrastructure.Persistence;

public sealed class InMemoryInvoiceStore : IInvoiceRepository
{
    private readonly ConcurrentDictionary<InvoiceId, Invoice> _invoices = new();

    public Task<Invoice?> GetByWorkOrderAsync(Guid workOrderId, CancellationToken ct = default) =>
        Task.FromResult(_invoices.Values.FirstOrDefault(i => i.WorkOrderId == workOrderId));

    public Task AddAsync(Invoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        _invoices[invoice.Id] = invoice;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Invoice>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Invoice>>(_invoices.Values.ToArray());
}
