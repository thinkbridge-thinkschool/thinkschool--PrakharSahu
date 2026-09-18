using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace QuotesApi.Messaging.Infrastructure;

/// <summary>
/// Getting events out of this process reliably: the transactional outbox (Day 20), the broker
/// transport (Day 19), and the subscribers that drain it.
/// </summary>
/// <remarks>
/// The outbox and the broker are one module rather than two because they are one guarantee.
/// The outbox exists only to make publishing survive a crash, and the relay is the half of it
/// that talks to the transport. Splitting them would put a module boundary through the middle
/// of a single at-least-once delivery story.
/// </remarks>
public static class MessagingModule
{
    public static IServiceCollection AddMessagingModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMessaging(configuration);
        services.AddOutbox(configuration);
        return services;
    }

    public static IEndpointRouteBuilder MapMessagingModule(this IEndpointRouteBuilder app)
    {
        app.MapMessagingEndpoints();
        app.MapOutboxEndpoints();
        return app;
    }
}
