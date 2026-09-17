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
        services.AddScoped<IWorkOrderRepository, EfWorkOrderRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<WorkOrderService>();

        // The inbound half of the scheduling saga.
        services.AddScoped<IIntegrationEventHandler<TechnicianReservationFailedV1>, ReservationFailedHandler>();

        // The success half of the same saga. Published since day one, subscribed to as of now --
        // without it nothing could tell a confirmed booking from one still in flight.
        services.AddScoped<IIntegrationEventHandler<TechnicianReservedV1>, ReservationConfirmedHandler>();

        services.AddHostedService<SlaSweeper>();

        // Watches for the saga reply that never arrives. Separate from SlaSweeper because it asks
        // a different question on a much shorter deadline -- see ReservationTimeoutSweeper.
        services.AddHostedService<ReservationTimeoutSweeper>();

        return services;
    }

    // The singleton is gone. That comment used to end "the moment this becomes a real store the
    // singleton disappears and the scoped lifetime starts meaning what it says" -- and this is
    // that moment. A scoped repository now resolves a scoped DbContext, so each request gets its
    // own change tracker and its own transaction boundary, which is what scoped was always
    // supposed to mean here.
    //
    // AddWorkManagementPersistence registers the DbContext itself. It is separate because the
    // connection string is the host's business, not the module's.
}
