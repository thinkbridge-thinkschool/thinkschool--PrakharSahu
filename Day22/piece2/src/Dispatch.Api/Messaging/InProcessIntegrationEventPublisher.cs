using Dispatch.SharedKernel;

namespace Dispatch.Api.Messaging;

/// <summary>
/// Delivers integration events to handlers in the same process.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class is the reason it is a modular monolith and not microservices.</b> The modules
/// are genuinely decoupled — they talk in versioned contracts, they never touch each other's
/// internals, and the architecture tests fail the build if they try. But delivery is a method
/// call, so there is no broker to run, no network to be partitioned, no serialisation format to
/// agree, and no distributed tracing needed to answer "why did nothing happen".
/// </para>
/// <para>
/// When one module genuinely needs to scale or deploy separately, this is the class that gets
/// replaced — with a Service Bus topic, an outbox relay, whatever fits — and no module changes,
/// because none of them ever knew which it was.
/// </para>
/// <para>
/// It lives in the host, not in SharedKernel, on purpose. Transport choice is a composition
/// decision. Putting it in the shared kernel would make every module depend on the answer.
/// </para>
/// </remarks>
public sealed class InProcessIntegrationEventPublisher(
    IServiceScopeFactory scopeFactory,
    ILogger<InProcessIntegrationEventPublisher> logger) : IIntegrationEventPublisher
{
    public async Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var eventType = integrationEvent.GetType();
        var handlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);

        // A fresh scope per event. The publisher is a singleton and handlers are scoped, and a
        // handler that ran inside the publishing request's scope would share its unit of work —
        // which would quietly re-couple the two modules through a transaction neither declared.
        using var scope = scopeFactory.CreateScope();
        var handlers = scope.ServiceProvider.GetServices(handlerType).ToArray();

        if (handlers.Length == 0)
        {
            // Not an error. A published event with no subscriber is the normal state of a
            // healthy contract — it means somebody may care later, not that something is broken.
            logger.LogDebug("No subscribers for {EventType}.", eventType.Name);
            return;
        }

        var handle = handlerType.GetMethod(nameof(IIntegrationEventHandler<IIntegrationEvent>.HandleAsync))!;

        foreach (var handler in handlers)
        {
            try
            {
                await (Task)handle.Invoke(handler, [integrationEvent, cancellationToken])!;
            }
            catch (Exception ex)
            {
                // One failing subscriber must not stop the others, and must not fail the publisher
                // — which is still inside the caller's request. That is the same isolation a real
                // broker gives you per-subscription, reproduced here so that moving to one later
                // does not change the failure semantics.
                //
                // The honest limitation: an event that fails here is LOST. A broker would retry
                // and eventually dead-letter it. Day 23's outbox is where that gets fixed.
                logger.LogError(
                    ex,
                    "{HandlerType} failed handling {EventType} ({EventId}). The event was dropped.",
                    handler!.GetType().Name, eventType.Name, integrationEvent.EventId);
            }
        }
    }
}
