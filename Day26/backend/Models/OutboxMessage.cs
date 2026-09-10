namespace QuotesApi.Models;

/// <summary>
/// One event, durably recorded in the same transaction as the change that caused it.
/// </summary>
/// <remarks>
/// <para><b>The problem this solves.</b> Day 19 committed the quote and then published the
/// event as two separate operations with no transaction across them — a dual write. Crash
/// between them and the quote exists while the event never happened: the projections are
/// permanently stale for that quote and nothing anywhere detects it. Reversing the order only
/// moves the failure: publish first and crash before the commit, and consumers act on a quote
/// that does not exist.</para>
///
/// <para><b>The fix.</b> There is exactly one thing you can make atomic with a database write,
/// and that is another database write. So the event becomes a row in this table, saved in the
/// same <c>SaveChangesAsync</c> as the quote. Either both land or neither does. A separate
/// relay reads the table afterwards and does the publishing, where a crash is survivable
/// because the row is still sitting there.</para>
///
/// <para><b>What it does not buy.</b> The relay can publish and then die before marking the
/// row sent, so the message goes out twice. The outbox guarantees <i>at-least-once</i>, never
/// exactly-once, and the second half of the guarantee lives in the consumer — Day 19's dedupe
/// on <see cref="Id"/>, which is deliberately the same value as the Service Bus MessageId.</para>
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>
    /// Identity of the event, and the idempotency key the consumer dedupes on.
    /// </summary>
    /// <remarks>
    /// Published as the Service Bus <c>MessageId</c>. Because it is generated once, when the
    /// row is written, every republish after a crash carries the <em>same</em> id — which is
    /// what lets a consumer recognise the duplicate rather than doing the work twice. Generate
    /// it at publish time instead and each retry looks like a brand-new event.
    /// </remarks>
    public Guid Id { get; set; }

    /// <summary>Event type, e.g. <c>quote.created</c>. Chooses the consumer's branch.</summary>
    public required string Type { get; set; }

    /// <summary>
    /// What the event was about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <b>not</b> a foreign key to <c>Quotes</c>, and that is a decision rather
    /// than an oversight. An outbox row must outlive its aggregate: if a quote is hard-deleted
    /// before the relay drains, a real FK would either block the delete or cascade the unsent
    /// event out of existence, and the event describing the deletion is precisely the one you
    /// cannot afford to lose.
    /// </para>
    /// <para>
    /// So the relationship is by value — recorded, indexed, joinable when both rows exist, and
    /// under no obligation to. The tradeoff is honest: the database will not enforce that this
    /// points at anything real.
    /// </para>
    /// </remarks>
    public required string AggregateType { get; set; }

    public required string AggregateId { get; set; }

    /// <summary>The serialised event. Opaque to the outbox; only the consumer reads it.</summary>
    public required string Payload { get; set; }

    /// <summary>
    /// When the domain change happened — not when it was published.
    /// </summary>
    /// <remarks>
    /// <b>UTC DateTime, not DateTimeOffset, and that is forced by the provider.</b> SQLite has
    /// no date type: EF stores DateTimeOffset as TEXT including the offset, so comparing or
    /// ordering two of them would be a lexicographic string comparison that is simply wrong
    /// once offsets differ. EF Core refuses to translate it rather than generate a query that
    /// quietly returns the wrong rows — which surfaced here as the relay throwing on every
    /// sweep and delivering nothing.
    ///
    /// These four columns exist to be filtered and ordered by the relay, so they are stored as
    /// UTC instants. There is no offset to preserve: "when the relay may next try" is not a
    /// value that belongs to a time zone.
    /// </remarks>
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// Null until the broker has accepted it. The entire definition of "pending".
    /// </summary>
    /// <remarks>
    /// Set <em>after</em> a successful publish, never before. Marking first and publishing
    /// second reintroduces exactly the dual write this table exists to remove, only now the
    /// lost message looks like a delivered one.
    /// </remarks>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>How many times publishing has been tried. Drives backoff and gives up loudly.</summary>
    public int Attempts { get; set; }

    /// <summary>Why the last attempt failed. A message, never a stack trace.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Claim expiry, so two relay instances cannot publish the same row at once.
    /// </summary>
    /// <remarks>
    /// A lease, not a lock. If the relay holding it dies, the lease simply expires and another
    /// instance picks the row up — no cleanup, no orphaned lock, no operator involvement. The
    /// cost is that a crash mid-publish means the row is retried, which is the duplicate the
    /// consumer already handles.
    /// </remarks>
    public DateTime? LockedUntil { get; set; }

    /// <summary>Which relay instance holds the lease. Diagnostic only.</summary>
    public string? LockedBy { get; set; }

    /// <summary>Do not retry before this. Null means eligible now.</summary>
    public DateTime? NextAttemptAt { get; set; }

    // ---------------------------------------------------------------------------------------
    // Day 26: the trace context, carried across a hop that would otherwise lose it.
    //
    // This is the single most important addition of the day, and the reason is that a database
    // row is a boundary the async context does not cross.
    //
    // Inside the API request, .NET's Activity flows automatically: any span created while
    // handling POST /api/quotes is a child of that request without anybody wiring it. But the
    // outbox exists precisely to DECOUPLE the write from the publish. The relay picks this row
    // up later, in a different process, on a different thread, with no ambient Activity at all —
    // so the publish, the broker delivery and the consumer's database write all appear as a
    // brand-new trace, unconnected to the request that caused them.
    //
    // The result is a trace that looks complete and is not. Nothing errors. The API trace ends
    // cleanly at "wrote the row"; a separate orphan trace begins at "published a message". They
    // are impossible to correlate after the fact, and the question the whole exercise exists to
    // answer — "this request was slow, where did the time go?" — becomes unanswerable at exactly
    // the hop most likely to be responsible.
    //
    // Storing the W3C traceparent on the row is what makes the two halves one trace. It is the
    // same idea the Service Bus SDK applies to message headers, done by hand because a database
    // row has no headers.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// W3C traceparent of the request that enqueued this message, e.g.
    /// <c>00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01</c>.
    /// </summary>
    /// <remarks>
    /// Nullable on purpose. Rows written before this column existed have none, and a message
    /// enqueued by a background process legitimately has no incoming trace. The relay treats
    /// absence as "start a new trace" rather than as an error — losing the link is bad, refusing
    /// to publish the message would be far worse.
    /// </remarks>
    public string? TraceParent { get; set; }

    /// <summary>
    /// W3C tracestate: vendor-specific trace data that travels alongside the traceparent.
    /// </summary>
    /// <remarks>
    /// Almost always null here, and carried anyway because dropping it silently breaks sampling
    /// decisions made upstream by another vendor's tracer. It costs one nullable column.
    /// </remarks>
    public string? TraceState { get; set; }

    public bool IsPending => ProcessedAt is null;
}
