using Dispatch.Scheduling.Contracts;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Application.Abstractions;
using Dispatch.WorkManagement.Application.WorkOrders;
using Dispatch.WorkManagement.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.WorkManagement.Infrastructure;

/// <summary>
/// Everything WorkManagement needs, registered in one call.
/// </summary>
/// <remarks>
/// <para>
/// The module owns its own composition. The host calls AddWorkManagement() and knows nothing
/// about what is inside -- which is what makes adding, removing or extracting a module a
/// one-line change at the top rather than an archaeology exercise through Program.cs.
/// </para>
/// <para>
/// It lives in Infrastructure because this is where the concrete types are. Registration is a
/// composition concern, and composition needs to see implementations; Application deliberately
/// cannot.
/// </para>
/// </remarks>
public static class WorkManagementModule
{
    public static IServiceCollection AddWorkManagement(this IServiceCollection services)
    {
        services.AddScoped<IWorkOrderRepository>(_ => Store);
        services.AddScoped<IUnitOfWork, InMemoryUnitOfWork>();
        services.AddScoped<WorkOrderService>();

        // The inbound half of the scheduling saga.
        services.AddScoped<IIntegrationEventHandler<TechnicianReservationFailedV1>, ReservationFailedHandler>();

        services.AddHostedService<SlaSweeper>();

        return services;
    }

    // A single instance behind a scoped registration, because the "database" is a dictionary and
    // a per-request one would forget everything between calls. The moment this becomes a real
    // store the singleton disappears and the scoped lifetime starts meaning what it says.
    private static readonly InMemoryWorkOrderStore Store = new();
}
