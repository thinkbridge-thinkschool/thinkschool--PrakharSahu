using QuotesApi.Models;

namespace QuotesApi.Jobs;

/// <summary>
/// Does the actual slow work for one kind of job.
/// </summary>
/// <remarks>
/// <para>
/// Handlers are resolved from a <b>scoped</b> service provider, one scope per job. That is
/// the rule that makes it safe for a handler to inject <c>AppDbContext</c> or anything else
/// registered as scoped, even though the worker draining the queue is a singleton.
/// </para>
/// <para>
/// The contract for the token is the whole point of the exercise: <b>a handler must observe
/// <paramref name="cancellationToken"/> and return promptly when it is signalled.</b> A
/// handler that ignores it cannot be cancelled, and worse, it holds up host shutdown until
/// the shutdown timeout expires and the process is killed mid-write.
/// </para>
/// </remarks>
public interface IJobHandler
{
    /// <summary>Matched against <see cref="Job.Type"/>. Case-insensitive.</summary>
    string JobType { get; }

    /// <summary>
    /// Runs the work. Returning normally means success; throwing means failure; throwing
    /// <see cref="OperationCanceledException"/> for the supplied token means cancelled.
    /// </summary>
    /// <returns>A short result string stored on the job and shown to whoever polls it.</returns>
    Task<string> HandleAsync(Job job, CancellationToken cancellationToken);
}
