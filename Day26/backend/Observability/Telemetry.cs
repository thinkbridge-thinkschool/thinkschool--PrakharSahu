using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace QuotesApi.Observability;

/// <summary>
/// The names and instruments the whole application traces and measures through.
/// </summary>
/// <remarks>
/// <para>
/// One place, because an <see cref="ActivitySource"/> only emits spans if its name has been
/// registered with <c>AddSource(...)</c> at startup. A source created inline somewhere in the
/// codebase and never registered is not an error and produces no warning — it silently emits
/// nothing, and the gap looks exactly like a propagation bug. Keeping every name here means the
/// registration list in Program.cs can be checked against it by eye.
/// </para>
/// <para>
/// The names are hierarchical (<c>QuotesApi.Api</c>, <c>QuotesApi.Outbox</c>, …) so a single
/// wildcard registration could cover them all, and so a KQL query can filter to one subsystem
/// without a list of magic strings.
/// </para>
/// </remarks>
public static class Telemetry
{
    /// <summary>The name reported to App Insights as <c>cloud_RoleName</c> for the API.</summary>
    public const string ApiRoleName = "quotes-api";

    /// <summary>The name reported to App Insights as <c>cloud_RoleName</c> for the worker.</summary>
    /// <remarks>
    /// Distinct from the API on purpose, and this is what makes the deliverable legible. App
    /// Insights draws its application map and its end-to-end transaction view by grouping spans
    /// on <c>cloud_RoleName</c>. Run both roles under one name and the trace still stitches
    /// correctly but renders as a single box — technically right and visually useless, because
    /// the thing being demonstrated is precisely that the trace crosses a process boundary.
    /// </remarks>
    public const string WorkerRoleName = "quotes-worker";

    // -------------------------------------------------------------------------------------------
    // Activity sources, one per subsystem.
    // -------------------------------------------------------------------------------------------

    /// <summary>Spans raised by API endpoint code, beneath the automatic ASP.NET Core span.</summary>
    public static readonly ActivitySource Api = new("QuotesApi.Api");

    /// <summary>Spans raised by the outbox relay — the hop that re-parents to a stored context.</summary>
    public static readonly ActivitySource Outbox = new("QuotesApi.Outbox");

    /// <summary>Spans raised by subscription consumers.</summary>
    public static readonly ActivitySource Messaging = new("QuotesApi.Messaging");

    /// <summary>Spans raised by the in-process background job pipeline.</summary>
    public static readonly ActivitySource Jobs = new("QuotesApi.Jobs");

    /// <summary>Every source name, for registration at startup.</summary>
    /// <remarks>
    /// Derived from the sources themselves rather than repeated as string literals, so a source
    /// added above cannot be forgotten below — the failure mode this whole class exists to avoid.
    /// </remarks>
    public static readonly string[] SourceNames =
    [
        Api.Name,
        Outbox.Name,
        Messaging.Name,
        Jobs.Name,

        // The Azure SDK's own spans — a Producer span per send, a Consumer span per receive.
        //
        // Registered both exactly and by wildcard. The wildcard alone produced nothing: no
        // Service Bus dependency appeared in App Insights at all, and the consumer spans arrived
        // as separate root traces rather than as children of the publish.
        //
        // Worth being honest about the outcome — adding the exact name did NOT fix that on its
        // own either. SubscriptionWorker now reads the traceparent out of the message itself
        // rather than relying on the SDK to have parented the activity. These stay registered
        // because a source that matches nothing costs nothing, and if the SDK spans do start
        // arriving they belong in the trace.
        "Azure.Messaging.ServiceBus",
        "Azure.Messaging.ServiceBus.*"
    ];

    // -------------------------------------------------------------------------------------------
    // Metrics.
    //
    // Traces answer "what happened to this one request". Metrics answer "what is happening in
    // general", and the two are not substitutes: reconstructing a p99 by querying every trace is
    // both expensive and, once sampling is on anywhere, wrong.
    // -------------------------------------------------------------------------------------------

    public const string MeterName = "QuotesApi";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Counts outbox rows successfully published, by event type.</summary>
    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("quotes.outbox.published", "messages",
            "Outbox rows published to the broker.");

    /// <summary>
    /// How long a message waited between being enqueued and being published.
    /// </summary>
    /// <remarks>
    /// The single most useful number the outbox produces, and one no HTTP metric contains. The
    /// API returns as soon as the row is committed, so request latency stays flat while this
    /// grows — a relay that has stalled is invisible from the front door until somebody notices
    /// the events stopped arriving.
    /// </remarks>
    public static readonly Histogram<double> OutboxLagSeconds =
        Meter.CreateHistogram<double>("quotes.outbox.lag", "s",
            "Seconds between an outbox row being written and being published.");

    /// <summary>Counts messages consumed from a subscription, by subscription and outcome.</summary>
    public static readonly Counter<long> MessagesConsumed =
        Meter.CreateCounter<long>("quotes.messaging.consumed", "messages",
            "Messages handled by a subscription consumer.");
}
