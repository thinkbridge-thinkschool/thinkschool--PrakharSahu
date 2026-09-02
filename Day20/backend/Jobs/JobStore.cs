using System.Collections.Concurrent;
using QuotesApi.Models;

namespace QuotesApi.Jobs;

public interface IJobStore
{
    void Add(Job job);
    bool TryGet(Guid id, out Job? job);
    IReadOnlyList<Job> List(int limit = 50);

    /// <summary>Called by the worker while a job runs, so it can be cancelled from outside.</summary>
    void RegisterRunning(Guid id, CancellationTokenSource cts);

    /// <summary>Called by the worker when a job leaves the running state, whatever the outcome.</summary>
    void ReleaseRunning(Guid id);

    /// <summary>
    /// Asks a running job to stop. Returns false when the job is unknown or already finished.
    /// </summary>
    /// <remarks>
    /// A request, not a guarantee — the job stops when its handler next observes the token.
    /// A handler that ignores its token cannot be cancelled by anything short of killing the
    /// process, which is why every handler here threads it all the way down.
    /// </remarks>
    bool TryRequestCancellation(Guid id);
}

/// <summary>
/// In-process job status, so a caller who got a 202 has somewhere to poll.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the database. Job status changes far more often than quotes do, and
/// writing every transition through EF would put the slow-work feature back on the hot path
/// it exists to keep clear.
/// </para>
/// <para>
/// The cost is stated plainly: <b>status does not survive a restart, and it is per-replica.</b>
/// A caller polling a job id after a deploy gets a 404 for work that really did run, and with
/// two replicas the caller may poll the instance that never saw the job. Both are the point
/// at which this design stops being adequate — see <c>EXERCISE.md</c>.
/// </para>
/// </remarks>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    /// <summary>
    /// Cap on retained history. Without one this dictionary is a memory leak with a
    /// respectable name — it grows for the life of the process and nothing ever removes an
    /// entry.
    /// </summary>
    private const int MaxRetained = 500;

    public void Add(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _jobs[job.Id] = job;

        if (_jobs.Count > MaxRetained)
        {
            EvictOldestCompleted();
        }
    }

    public bool TryGet(Guid id, out Job? job) => _jobs.TryGetValue(id, out job);

    public IReadOnlyList<Job> List(int limit = 50) =>
        _jobs.Values.OrderByDescending(j => j.CreatedAt).Take(limit).ToList();

    public void RegisterRunning(Guid id, CancellationTokenSource cts) => _running[id] = cts;

    public void ReleaseRunning(Guid id)
    {
        if (_running.TryRemove(id, out var cts))
        {
            cts.Dispose();
        }
    }

    public bool TryRequestCancellation(Guid id)
    {
        if (!_running.TryGetValue(id, out var cts) || cts.IsCancellationRequested)
        {
            return false;
        }

        // The CTS can be disposed by ReleaseRunning between the lookup and this call, if the
        // job finishes on its own at exactly the wrong moment. Losing that race means the job
        // completed, which is the outcome the caller wanted anyway.
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Drops the oldest terminal jobs. Never touches Queued or Running — evicting those would
    /// lose the status of work that is still going to happen.
    /// </summary>
    private void EvictOldestCompleted()
    {
        var evictable = _jobs.Values
            .Where(j => j.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
            .OrderBy(j => j.CompletedAt ?? j.CreatedAt)
            .Take(_jobs.Count - MaxRetained + 50)   // a batch, so this is not run on every add
            .Select(j => j.Id);

        foreach (var id in evictable)
        {
            _jobs.TryRemove(id, out _);
        }
    }
}
