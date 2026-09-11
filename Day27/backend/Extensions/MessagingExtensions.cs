using Azure.Core;
using Azure.Identity;
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
                "Messaging is DISABLED: neither {Section}:FullyQualifiedNamespace nor {Section}:ConnectionString is set. Quote events will not be published.",
                ServiceBusOptions.SectionName, ServiceBusOptions.SectionName);

            services.AddSingleton<IEventPublisher, NoOpEventPublisher>();

            // Must be registered even with messaging off. Minimal APIs infer an unregistered
            // interface parameter as the request body, and a DELETE cannot have one — so
            // omitting this does not disable the DLQ endpoints, it prevents the application
            // from starting at all. See DisabledDeadLetterReader.
            services.AddSingleton<IDeadLetterReader, DisabledDeadLetterReader>();
            return services;
        }

        // One client for the process. ServiceBusClient owns an AMQP connection and is
        // thread-safe by design — senders, receivers and processors are all created from it
        // and share that connection. One client per operation would open a TCP connection per
        // publish, which is the single easiest way to make a fast broker look slow.
        // ---------------------------------------------------------------------------------
        // Day 26: a token, not a connection string.
        //
        // The namespace this connects to has `disableLocalAuth: true`, so its own SAS keys are
        // rejected — a leaked one is worth nothing. That is Day 25's model applied to a process
        // that happens to run on a laptop.
        //
        // The intent was "DefaultAzureCredential works in both places with no branch". It does
        // not, and there IS a branch below as a result — see ServiceBusOptions
        // .UseAzureCliCredential for the measured reason. Worth stating plainly rather than
        // leaving a comment that describes what was hoped for.
        //
        // The connection-string path is kept because Day 19-22's tests construct options
        // directly and would otherwise stop compiling. It is last, not first.
        // ---------------------------------------------------------------------------------
        if (options.UsesManagedIdentity)
        {
            // AzureCliCredential directly, not DefaultAzureCredential, when running locally.
            // See ServiceBusOptions.UseAzureCliCredential for why the chain cannot be relied on
            // off Azure: its managed-identity link throws AuthenticationFailedException rather
            // than CredentialUnavailableException, which aborts the chain instead of advancing
            // it to the CLI credential.
            TokenCredential credential = options.UseAzureCliCredential
                ? new AzureCliCredential()
                : new DefaultAzureCredential();

            bootstrapLogger?.LogInformation(
                "Service Bus auth: {Credential} against {Namespace}.",
                options.UseAzureCliCredential ? "AzureCliCredential" : "DefaultAzureCredential",
                options.FullyQualifiedNamespace);

            services.AddSingleton(_ => new ServiceBusClient(
                options.FullyQualifiedNamespace,
                credential));
        }
        else
        {
            services.AddSingleton(_ => new ServiceBusClient(options.ConnectionString));
        }

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
