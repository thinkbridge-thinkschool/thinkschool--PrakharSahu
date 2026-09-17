using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Dispatch.Api.Security;

/// <summary>
/// The controls the Day 31 security re-check found missing.
/// </summary>
/// <remarks>
/// <para>
/// This is the Day 27 hardening pass applied to the capstone, minus the half that needs an
/// identity provider. What is deliberately <b>not</b> here is authentication and authorization:
/// Dispatch has no notion of a caller yet, and bolting on a scheme before the roles exist would
/// produce security theatre. That is build-plan day 8, and it is recorded as an open gap rather
/// than quietly skipped.
/// </para>
/// <para>
/// Everything below costs nothing to add now and is expensive to retrofit after a client depends
/// on the current behaviour.
/// </para>
/// </remarks>
public static class ApiHardening
{
    /// <summary>Largest request body accepted on any endpoint, in bytes.</summary>
    /// <remarks>
    /// 64 KB. Kestrel's default is 30 MB, which is sensible for an application that accepts file
    /// uploads and absurd for one whose largest legitimate request is a work order with a
    /// 500-character summary. A limit chosen for this workload rather than inherited.
    /// </remarks>
    public const long MaxRequestBodyBytes = 64 * 1024;

    /// <summary>Rate-limit policy applied to the whole API.</summary>
    public const string GlobalPolicy = "global";

    public static IServiceCollection AddApiHardening(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // 300 a minute per caller. Loose on purpose: this exists to stop a runaway client or
            // a naive retry loop, not to police normal use. A limiter set near real traffic
            // becomes an outage generator the first time the business has a busy morning.
            //
            // QueueLimit is zero. Queueing a rejected request holds a connection and a thread --
            // the very resources the limiter is protecting -- so refusing immediately with 429 is
            // both cheaper and more honest to the caller.
            options.AddPolicy(GlobalPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 300,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            // Without this a well-behaved client retries immediately and becomes
            // indistinguishable from a badly-behaved one.
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    /// <summary>
    /// How a caller is identified for rate limiting.
    /// </summary>
    /// <remarks>
    /// The remote address, because there is no authenticated subject yet. That is a weak signal
    /// and worth naming: everyone behind one corporate NAT shares a bucket and throttles each
    /// other, while an attacker with a /64 of IPv6 has effectively unlimited buckets. When
    /// authentication arrives on day 8 this should partition on the subject first and fall back
    /// to the address only for anonymous endpoints.
    ///
    /// `X-Forwarded-For` is deliberately not consulted. It is caller-supplied unless a trusted
    /// proxy overwrote it, so trusting it would let anyone pick their own partition -- turning
    /// the rate limiter off with a header.
    /// </remarks>
    private static string PartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Response headers a scanner looks for, and removal of the one that leaks.
    /// </summary>
    /// <remarks>
    /// Registered FIRST in the pipeline, so the headers are present on the 404s, 429s and 500s
    /// produced further down -- precisely the responses an attacker is most interested in, and
    /// exactly the ones that miss headers added late.
    /// </remarks>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            // Stops a browser second-guessing Content-Type. Without it a JSON response whose body
            // begins with HTML can be sniffed and rendered, turning a stored string into stored
            // XSS for whatever renders this API's output.
            headers["X-Content-Type-Options"] = "nosniff";

            // A JSON API has nothing worth framing, so the strictest value is free.
            headers["X-Frame-Options"] = "DENY";

            // `default-src 'none'` is the correct policy for an API that returns no HTML, no
            // scripts and no styles: this response should never load anything at all.
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

            // Do not leak a URL containing a work order id to whatever site the user visits next.
            headers["Referrer-Policy"] = "no-referrer";

            // The Spectre-era isolation header that matters for a JSON API: refuse to hand this
            // response to a cross-origin document at all.
            headers["Cross-Origin-Resource-Policy"] = "same-origin";

            // `Server: Kestrel` is free reconnaissance -- it names the stack, which narrows the
            // set of CVEs worth trying. Removing it is not a control, since anyone determined
            // will fingerprint the behaviour instead; it raises the cost of the cheap automated
            // end of the spectrum, which is most of it.
            headers.Remove("Server");

            await next();
        });
}
