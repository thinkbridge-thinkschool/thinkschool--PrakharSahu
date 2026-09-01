using Azure.Messaging.ServiceBus;
using QuotesApi.Messaging;
using QuotesApi.Messaging.Handlers;

namespace QuotesApi.Extensions;

public static class MessagingExtensions
{
    /// <summary>
    /// Registers publisher, consumers and dead-letter reader — or a no-op publisher when no
    /// connection string is configured.
    /// </summary>
    /// <remarks>
    /// Absent configuration disables the feature instead of failing the boot, the same switch
    /// Day 17 used for caller identity. The Week-1 API, the Day 18 job tests and a bare
    /// <c>dotnet run</c> all have to keep working on a machine with no broker; making
    /// messaging mandatory would break every one of them to add a feature none of them use.
    /// </remarks>
    public static IServiceCollection AddMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        ILogger? bootstrapLogger = null)
    {
        var section = configuration.GetSection(ServiceBusOptions.SectionName);
        services.Configure<ServiceBusOptions>(section);

        var options = section.Get<ServiceBusOptions>() ?? new ServiceBusOptions();

        // The projection store is a singleton either way, so /api/messaging/projections
        // answers with an empty result rather than a 500 when messaging is off.
        services.AddSingleton<IProjectionStore, InMemoryProjectionStore>();
        services.AddSingleton<IProcessedMessageTracker, InMemoryProcessedMessageTracker>();

        if (!options.Enabled)
        {
            bootstrapLogger?.LogWarning(
                "Messaging is DISABLED: {Section}:ConnectionString is not set. Quote events will not be published.",
                ServiceBusOptions.SectionName);

            services.AddSingleton<IEventPublisher, NoOpEventPublisher>();
            return services;
        }

        // One client for the process. ServiceBusClient owns an AMQP connection and is
        // thread-safe by design — senders, receivers and processors are all created from it
        // and share that connection. One client per operation would open a TCP connection per
        // publish, which is the single easiest way to make a fast broker look slow.
        services.AddSingleton(_ => new ServiceBusClient(options.ConnectionString));

        services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();
        services.AddSingleton<IDeadLetterReader, ServiceBusDeadLetterReader>();

        // Scoped, so each message gets a handler with its own scoped dependencies — the same
        // captive-dependency rule Day 18 ran into head-first.
        services.AddScoped<ISubscriptionHandler, AuditProjectionHandler>();
        services.AddScoped<ISubscriptionHandler, SearchIndexHandler>();

        services.AddHostedService<SubscriptionWorker>();

        bootstrapLogger?.LogInformation(
            "Messaging ENABLED: topic '{Topic}', subscriptions '{Audit}' and '{Search}', "
            + "{Consumers} consumers each at concurrency {Concurrency}.",
            options.TopicName, options.AuditSubscription, options.SearchIndexSubscription,
            options.ConsumersPerSubscription, options.MaxConcurrentCalls);

        return services;
    }
}
