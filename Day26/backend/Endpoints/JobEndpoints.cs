using QuotesApi.Jobs;
using QuotesApi.Models;
using QuotesApi.Services;

namespace QuotesApi.Endpoints;

public sealed record CreateJobRequest(string Type, string? Payload);

/// <summary>
/// The shape a job is served as. Deliberately a projection rather than the entity, so adding
/// an internal field to <see cref="Job"/> cannot silently widen the public API.
/// </summary>
public sealed record JobResponse(
    Guid Id,
    string Type,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    double? QueueLatencyMs,
    double? DurationMs,
    string? Progress,
    string? Result,
    string? Error)
{
    public static JobResponse From(Job job) => new(
        job.Id,
        job.Type,
        job.Status.ToString(),
        job.CreatedAt,
        job.StartedAt,
        job.CompletedAt,
        job.QueueLatency?.TotalMilliseconds,
        job.Duration?.TotalMilliseconds,
        job.Progress,
        job.Result,
        job.Error);
}

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs");

        // ---------------------------------------------------------------------------------
        // POST /api/jobs — hand work off and return immediately.
        //
        // The entire point of the day: this endpoint does no slow work. It validates, drops a
        // job on the queue and answers 202 Accepted with a Location header pointing at where
        // the result will eventually appear. Response time is independent of how long the job
        // takes.
        //
        // Authorization is required because an anonymous caller who can enqueue expensive
        // work has a denial-of-service primitive. The bounded queue limits the blast radius;
        // requiring a token removes the anonymous half of it.
        // ---------------------------------------------------------------------------------
        group.MapPost("/", async (
            CreateJobRequest request,
            IJobQueue queue,
            IJobStore store,
            IEnumerable<IJobHandler> handlers,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Type))
            {
                return Results.BadRequest(new DomainError("A job type is required."));
            }

            // Rejected here rather than at dequeue time. Discovering an unknown type in the
            // worker means the caller already received a 202 for a job that can only fail.
            var known = handlers.Any(h =>
                string.Equals(h.JobType, request.Type, StringComparison.OrdinalIgnoreCase));

            if (!known)
            {
                return Results.BadRequest(new DomainError(
                    $"Unknown job type '{request.Type}'. Known types: "
                    + string.Join(", ", handlers.Select(h => h.JobType).Order())));
            }

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Type = request.Type,
                Payload = request.Payload,
                CreatedAt = clock.UtcNow
            };

            store.Add(job);

            if (!await queue.EnqueueAsync(job, cancellationToken))
            {
                // The queue is closed, which means the host is shutting down. 503 with
                // Retry-After is the honest answer: try again once the new instance is up.
                job.Status = JobStatus.Cancelled;
                job.Error = "The service is shutting down and is not accepting new jobs.";
                job.CompletedAt = clock.UtcNow;

                return Results.Json(
                    new DomainError("The service is shutting down. Retry shortly."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Accepted($"/api/jobs/{job.Id}", JobResponse.From(job));
        }).RequireAuthorization();

        // GET /api/jobs/{id} — where the 202's Location header points.
        group.MapGet("/{id:guid}", (Guid id, IJobStore store) =>
            store.TryGet(id, out var job) && job is not null
                ? Results.Ok(JobResponse.From(job))
                : Results.NotFound(new DomainError(
                    "No such job. Job history is in-process and does not survive a restart.")));

        // GET /api/jobs — most recent first, plus the queue depth.
        //
        // Depth is the number worth watching: a rising queue means the worker is not keeping
        // up, and that is invisible in per-job durations, which stay flat right up until the
        // queue is full and enqueues start blocking.
        group.MapGet("/", (IJobStore store, IJobQueue queue, int? limit) => Results.Ok(new
        {
            queueDepth = queue.Count,
            jobs = store.List(Math.Clamp(limit ?? 50, 1, 200)).Select(JobResponse.From)
        }));

        // ---------------------------------------------------------------------------------
        // DELETE /api/jobs/{id} — ask a running job to stop.
        //
        // 202, not 204: cancellation is a request, not an instruction. The job stops when its
        // handler next observes the token, and a handler that ignores it will not stop at all.
        // ---------------------------------------------------------------------------------
        group.MapDelete("/{id:guid}", (Guid id, IJobStore store) =>
        {
            if (!store.TryGet(id, out var job) || job is null)
            {
                return Results.NotFound(new DomainError("No such job."));
            }

            if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
            {
                return Results.Conflict(new DomainError(
                    $"Job is already {job.Status} and cannot be cancelled."));
            }

            if (!store.TryRequestCancellation(id))
            {
                // Queued but not yet running: there is no token to signal. Marking it here
                // would race the worker picking it up, so this reports the state honestly
                // instead of pretending.
                return Results.Conflict(new DomainError(
                    "The job has not started yet, so it cannot be cancelled. Retry once it is Running."));
            }

            return Results.Accepted($"/api/jobs/{id}", JobResponse.From(job));
        }).RequireAuthorization();
    }
}
