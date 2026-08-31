namespace QuotesApi.Models;

/// <summary>Where a job is in its lifecycle.</summary>
/// <remarks>
/// <para>
/// <see cref="Queued"/> → <see cref="Running"/> → one of the three terminal states. The
/// terminal states are deliberately distinct rather than a single "Finished" with a flag:
/// a caller polling for a result needs to tell "it worked", "it broke" and "the host shut
/// down before it got there" apart, and only the last of those is worth retrying blindly.
/// </para>
/// </remarks>
public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,

    /// <summary>
    /// The job was stopped before finishing — either the caller asked, or the host began
    /// shutting down while it was mid-flight.
    /// </summary>
    Cancelled
}

/// <summary>
/// A unit of slow work handed off from a request thread.
/// </summary>
/// <remarks>
/// <para>
/// Mutable and reference-typed on purpose. The same instance is held by the queue, the
/// worker and the store, so a status change made by the worker is immediately visible to a
/// caller polling <c>GET /api/jobs/{id}</c> without a write back through the store.
/// </para>
/// <para>
/// That works because this store is in-process. It is exactly the assumption that breaks
/// the moment the queue becomes durable or the app runs more than one replica — see
/// <c>Day18/EXERCISE.md</c> on when to reach for Hangfire instead.
/// </para>
/// </remarks>
public sealed class Job
{
    public required Guid Id { get; init; }

    /// <summary>Selects the handler. Matched against <c>IJobHandler.JobType</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Opaque to the queue and the worker; only the handler interprets it.</summary>
    public string? Payload { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Free-text progress, so a slow job is not a black box while it runs.</summary>
    public string? Progress { get; set; }

    /// <summary>Set on <see cref="JobStatus.Succeeded"/>.</summary>
    public string? Result { get; set; }

    /// <summary>
    /// Set on <see cref="JobStatus.Failed"/>. A message, never an exception or a stack trace:
    /// this is served over HTTP, and a stack trace names internal types and file paths.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// How long the job waited between being enqueued and being picked up. The number that
    /// tells you whether the worker is keeping up, which no per-job duration ever will.
    /// </summary>
    public TimeSpan? QueueLatency => StartedAt is null ? null : StartedAt - CreatedAt;

    public TimeSpan? Duration =>
        StartedAt is null || CompletedAt is null ? null : CompletedAt - StartedAt;
}
