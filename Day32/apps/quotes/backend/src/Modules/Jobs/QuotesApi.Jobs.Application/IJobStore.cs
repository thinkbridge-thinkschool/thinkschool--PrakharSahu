using QuotesApi.Jobs.Domain;

namespace QuotesApi.Jobs.Application;

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
