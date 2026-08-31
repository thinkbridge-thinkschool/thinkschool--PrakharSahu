using System.Text.Json;
using QuotesApi.Models;

namespace QuotesApi.Jobs.Handlers;

/// <summary>
/// A job whose duration and outcome are dictated by its payload.
/// </summary>
/// <remarks>
/// Exists so every state in the lifecycle — succeeded, failed, cancelled mid-flight — can be
/// produced on demand from a curl command, without waiting for a real failure to occur or
/// seeding a database to make a job slow. <c>verify.sh</c> and the shutdown tests both drive
/// this handler.
/// </remarks>
public sealed class SimulatedWorkHandler : IJobHandler
{
    public const string Type = "simulate";

    private readonly ILogger<SimulatedWorkHandler> _logger;

    public SimulatedWorkHandler(ILogger<SimulatedWorkHandler> logger) => _logger = logger;

    public string JobType => Type;

    private sealed record Options(int? DurationMs, bool? ShouldFail);

    public async Task<string> HandleAsync(Job job, CancellationToken cancellationToken)
    {
        var options = Parse(job.Payload);
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(options?.DurationMs ?? 3000, 0, 120_000));
        var shouldFail = options?.ShouldFail ?? false;

        _logger.LogInformation(
            "Simulated job {JobId}: {DurationMs}ms, failing={ShouldFail}.",
            job.Id, duration.TotalMilliseconds, shouldFail);

        // Sliced into ten steps rather than one long Delay, so the job reports progress and
        // reacts to cancellation promptly instead of only at the end.
        const int steps = 10;
        var slice = duration / steps;

        for (var step = 1; step <= steps; step++)
        {
            await Task.Delay(slice, cancellationToken);
            job.Progress = $"Step {step} of {steps}.";
        }

        if (shouldFail)
        {
            // Thrown, not returned as a result string. The processor's catch is what maps an
            // exception to JobStatus.Failed, and going through it keeps the demo honest about
            // how a real handler failure behaves.
            throw new InvalidOperationException("Simulated failure, requested by the job payload.");
        }

        return $"Completed {duration.TotalMilliseconds:0}ms of simulated work.";
    }

    /// <summary>Bad payloads are ignored rather than fatal — the defaults are always usable.</summary>
    private static Options? Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        try
        {
            return JsonSerializer.Deserialize<Options>(
                payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
