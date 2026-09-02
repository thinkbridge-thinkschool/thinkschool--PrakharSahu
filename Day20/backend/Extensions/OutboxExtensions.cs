using QuotesApi.Outbox;

namespace QuotesApi.Extensions;

public static class OutboxExtensions
{
    public static IServiceCollection AddOutbox(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));

        // Scoped, and it must be. The writer has to share the request's AppDbContext instance
        // so its row joins the same transaction as the domain change. Registering it as a
        // singleton would give it a captive DbContext and the outbox row would commit
        // separately — the dual write again, now hidden inside the thing meant to prevent it.
        services.AddScoped<IOutboxWriter, OutboxWriter>();

        // Singleton, so a fault armed over HTTP is visible to the relay's own scope.
        services.AddSingleton<OutboxFaults>();

        services.AddHostedService<OutboxRelay>();

        return services;
    }
}
