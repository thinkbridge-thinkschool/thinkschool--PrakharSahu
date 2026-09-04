using Dispatch.Billing.Application.Invoices;
using Dispatch.Billing.Infrastructure.Persistence;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Billing.Infrastructure;

public static class BillingModule
{
    public static IServiceCollection AddBilling(this IServiceCollection services)
    {
        services.AddScoped<IInvoiceRepository>(_ => Store);
        services.AddScoped<IIntegrationEventHandler<WorkOrderCompletedV1>, WorkOrderCompletedHandler>();

        return services;
    }

    private static readonly InMemoryInvoiceStore Store = new();
}
