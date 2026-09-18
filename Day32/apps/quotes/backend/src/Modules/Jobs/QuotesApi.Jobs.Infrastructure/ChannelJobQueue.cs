using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuotesApi.Jobs.Application;
using QuotesApi.Jobs.Domain;

namespace QuotesApi.Jobs.Infrastructure;

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
