using QuotesApi.Jobs.Domain;

namespace QuotesApi.Jobs.Application;

public interface IJobQueue
{
    /// <summary>
    /// Hands a job to the worker. Completes once the job is accepted, not once it has run.
    /// </summary>
    /// <remarks>
    /// Returns <c>false</c> when the queue is closed, which happens during shutdown. The
    /// caller must not treat that as a transient failure to retry — nothing will drain it.
    /// </remarks>
    ValueTask<bool> EnqueueAsync(Job job, CancellationToken cancellationToken);

    /// <summary>Streams jobs to the single consumer until the queue is closed and drained.</summary>
    IAsyncEnumerable<Job> DequeueAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops accepting new work. Already-queued jobs still drain.
    /// </summary>
    /// <remarks>
    /// The two halves of a clean shutdown are separable, and this is the first one: close the
    /// front door, then finish what is already inside. Calling this is what lets
    /// <see cref="DequeueAllAsync"/> terminate naturally instead of being torn down.
    /// </remarks>
    void Complete();

    /// <summary>Approximate depth. For metrics and the health endpoint, never for control flow.</summary>
    int Count { get; }
}

public sealed class JobQueueOptions
{
    /// <summary>
    /// Maximum jobs waiting to be picked up.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. An unbounded channel turns a burst of requests into unbounded
    /// memory growth and the process dies with an OOM that names nothing useful. Bounded, the
    /// pressure surfaces at the enqueue call where it can be reported to the caller.
    /// </remarks>
    public int Capacity { get; set; } = 100;

    /// <summary>
    /// How long a job that is already running is given to finish once shutdown begins,
    /// before its cancellation token is signalled.
    /// </summary>
    /// <remarks>
    /// Must stay comfortably below <c>HostOptions.ShutdownTimeout</c>. If the grace period is
    /// the longer of the two, the host stops waiting and kills the process while the job is
    /// still working — the grace period then achieves nothing except delaying the kill.
    /// Program.cs sets ShutdownTimeout from this value plus a margin so the two cannot drift
    /// apart.
    /// </remarks>
    public TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(10);
}
