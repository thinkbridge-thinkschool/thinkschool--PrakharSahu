using System.Threading.Channels;
using Microsoft.Extensions.Options;
using QuotesApi.Models;

namespace QuotesApi.Jobs;

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

/// <summary>
/// An in-memory job queue over <see cref="Channel{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Channel&lt;T&gt;</c> rather than <c>ConcurrentQueue&lt;T&gt;</c> plus a
/// <c>SemaphoreSlim</c>, because the consumer needs to <em>wait</em> for work without
/// spinning, and a channel already models "wait until an item arrives, or until the producer
/// says there will never be another". That second half is what makes graceful shutdown fall
/// out for free instead of needing a sentinel value or a separate flag.
/// </para>
/// <para>
/// Single reader, many writers: one worker drains it, every request thread can enqueue.
/// Declaring that lets the channel skip the synchronisation a multi-reader channel needs.
/// </para>
/// </remarks>
public sealed class ChannelJobQueue : IJobQueue
{
    private readonly Channel<Job> _channel;
    private readonly ILogger<ChannelJobQueue> _logger;

    public ChannelJobQueue(IOptions<JobQueueOptions> options, ILogger<ChannelJobQueue> logger)
    {
        _logger = logger;

        _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(options.Value.Capacity)
        {
            // Wait, not DropWrite or DropOldest. Dropping would let the API answer 202
            // Accepted for a job that was silently thrown away — the caller polls a job id
            // that never runs and never fails. Waiting applies backpressure to the caller
            // instead, which is honest and observable.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public int Count => _channel.Reader.Count;

    public async ValueTask<bool> EnqueueAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        try
        {
            await _channel.Writer.WriteAsync(job, cancellationToken);
            return true;
        }
        catch (ChannelClosedException)
        {
            // The host is shutting down. Not an error worth throwing at the caller — the
            // endpoint turns it into a 503, which is exactly what it is.
            _logger.LogWarning("Rejected job {JobId} ({JobType}): the queue is closed.", job.Id, job.Type);
            return false;
        }
    }

    public IAsyncEnumerable<Job> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => _channel.Writer.TryComplete();
}
