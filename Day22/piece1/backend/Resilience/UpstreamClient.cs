using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Registry;
using Polly.Timeout;

namespace QuotesApi.Resilience;

/// <summary>Why a call ended the way it did. The vocabulary the proof is written in.</summary>
public enum UpstreamOutcome
{
    Succeeded,

    /// <summary>The dependency answered, and its answer was a failure.</summary>
    UpstreamFailed,

    /// <summary>Rejected by the breaker without touching the network. Costs nothing.</summary>
    CircuitOpen,

    /// <summary>Rejected by the bulkhead: no slot and no room in its queue.</summary>
    BulkheadRejected,

    /// <summary>An attempt, or the whole operation, ran out of time.</summary>
    TimedOut
}

public sealed record UpstreamCallResult(
    UpstreamOutcome Outcome,
    int? StatusCode,
    double ElapsedMs,
    string Detail)
{
    public bool Ok => Outcome == UpstreamOutcome.Succeeded;
}

/// <summary>
/// The one place outbound calls to the upstream dependency are made.
/// </summary>
/// <remarks>
/// <para>
/// A single choke point on purpose. A pipeline only protects what goes through it, and a second
/// code path that quietly news up an <c>HttpClient</c> shares the dependency's failures without
/// sharing its breaker, its bulkhead or its budget. Centralising the call is what makes the
/// guarantees hold process-wide instead of per-call-site.
/// </para>
/// <para>
/// Note what this class does NOT do: it never decides whether to retry. It states a fact the
/// caller is uniquely able to state - whether the operation is safe to repeat - and the pipeline
/// decides what to do with that.
/// </para>
/// </remarks>
public sealed class UpstreamClient
{
    private readonly HttpClient _http;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly ResilienceEventLog _log;
    private readonly ILogger<UpstreamClient> _logger;

    public UpstreamClient(
        HttpClient http,
        ResiliencePipelineProvider<string> pipelines,
        ResilienceEventLog log,
        ILogger<UpstreamClient> logger)
    {
        _http = http;
        _pipeline = pipelines.GetPipeline<HttpResponseMessage>(UpstreamResilience.PipelineKey);
        _log = log;
        _logger = logger;
    }

    /// <summary>A safe-to-repeat read. Gets retries.</summary>
    public Task<UpstreamCallResult> ReadAsync(CancellationToken cancellationToken) =>
        CallAsync(HttpMethod.Get, "/upstream/quote-of-the-day", cancellationToken);

    /// <summary>
    /// A write with side effects. Gets the timeout, the breaker and the bulkhead - never retries.
    /// </summary>
    /// <remarks>
    /// Exists so the idempotency rule can be demonstrated rather than asserted: the same
    /// pipeline, the same failing dependency, and a visibly different number of attempts.
    /// </remarks>
    public Task<UpstreamCallResult> NotifyAsync(CancellationToken cancellationToken) =>
        CallAsync(HttpMethod.Post, "/upstream/notify", cancellationToken);

    private async Task<UpstreamCallResult> CallAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        var idempotent = IsIdempotent(method);
        var stopwatch = Stopwatch.StartNew();

        // Pooled rather than newed up. The context is allocated on every outbound call, and on a
        // hot path that allocation is pure waste. It must be returned in a finally.
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(UpstreamResilience.IsIdempotent, idempotent);

        try
        {
            _log.CountUpstreamCall();

            var response = await _pipeline.ExecuteAsync(
                static async (ctx, state) =>
                {
                    // A fresh request per attempt: an HttpRequestMessage cannot be sent twice,
                    // so reusing one across retries throws on the second attempt and the retry
                    // strategy silently stops working.
                    using var request = new HttpRequestMessage(state.Method, state.Path);
                    return await state.Http.SendAsync(request, ctx.CancellationToken);
                },
                context,
                (Http: _http, Method: method, Path: path));

            using (response)
            {
                var status = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    return new UpstreamCallResult(
                        UpstreamOutcome.Succeeded, status, stopwatch.Elapsed.TotalMilliseconds, "ok");
                }

                _log.CountUpstreamFailure();
                return new UpstreamCallResult(
                    UpstreamOutcome.UpstreamFailed,
                    status,
                    stopwatch.Elapsed.TotalMilliseconds,
                    $"upstream answered {status} after exhausting the pipeline");
            }
        }
        catch (BrokenCircuitException)
        {
            // Not a network failure - the call never left the process. This is the breaker doing
            // its job, and it is measured separately because "1000 rejections" and "1000 failed
            // calls" cost wildly different amounts and mean different things.
            _log.CountBreakerRejection();
            _log.Record("circuit-breaker", "rejected", "call short-circuited while open");

            return new UpstreamCallResult(
                UpstreamOutcome.CircuitOpen,
                (int)HttpStatusCode.ServiceUnavailable,
                stopwatch.Elapsed.TotalMilliseconds,
                "circuit is open; the call was rejected without reaching the dependency");
        }
        catch (RateLimiterRejectedException)
        {
            _log.CountBulkheadRejection();
            _log.Record("bulkhead", "rejected", "no permit and the queue is full");

            return new UpstreamCallResult(
                UpstreamOutcome.BulkheadRejected,
                (int)HttpStatusCode.ServiceUnavailable,
                stopwatch.Elapsed.TotalMilliseconds,
                "bulkhead is full; shed to protect the rest of the process");
        }
        catch (TimeoutRejectedException)
        {
            _log.CountUpstreamFailure();

            return new UpstreamCallResult(
                UpstreamOutcome.TimedOut,
                (int)HttpStatusCode.GatewayTimeout,
                stopwatch.Elapsed.TotalMilliseconds,
                "the operation exceeded its total timeout");
        }
        catch (HttpRequestException ex)
        {
            _log.CountUpstreamFailure();
            _logger.LogWarning(ex, "Upstream call {Method} {Path} failed at the transport layer.", method, path);

            return new UpstreamCallResult(
                UpstreamOutcome.UpstreamFailed,
                (int)HttpStatusCode.BadGateway,
                stopwatch.Elapsed.TotalMilliseconds,
                ex.Message);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// RFC 9110: GET, HEAD, PUT, DELETE, OPTIONS and TRACE are idempotent. POST and PATCH are not.
    /// </summary>
    /// <remarks>
    /// Idempotent means repeating the request leaves the server in the same state as sending it
    /// once. Not that the response is identical - a GET whose body changes between calls is still
    /// idempotent, because reading it again changed nothing. That distinction is what makes the
    /// method a sound basis for the retry decision.
    /// </remarks>
    public static bool IsIdempotent(HttpMethod method) =>
        method == HttpMethod.Get
        || method == HttpMethod.Head
        || method == HttpMethod.Put
        || method == HttpMethod.Delete
        || method == HttpMethod.Options
        || method == HttpMethod.Trace;
}
