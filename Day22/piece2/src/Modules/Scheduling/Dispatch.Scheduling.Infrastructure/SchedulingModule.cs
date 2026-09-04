using Dispatch.Scheduling.Application.Reservations;
using Dispatch.Scheduling.Infrastructure.Persistence;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Scheduling.Infrastructure;

public static class SchedulingModule
{
    public static IServiceCollection AddScheduling(this IServiceCollection services)
    {
        services.AddScoped<IReservationRepository>(_ => Store);

        // Scheduling's entire inbound surface: two subscriptions and no HTTP endpoints of its
        // own yet. A module that only reacts is a perfectly good module.
        services.AddScoped<IIntegrationEventHandler<WorkOrderScheduledV1>, WorkOrderScheduledHandler>();
        services.AddScoped<IIntegrationEventHandler<WorkOrderReleasedV1>, WorkOrderReleasedHandler>();

        return services;
    }

    private static readonly InMemoryReservationStore Store = new();
}
