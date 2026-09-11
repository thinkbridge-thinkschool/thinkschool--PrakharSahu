using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

namespace QuotesApi.Security;

/// <summary>
/// The Day 27 hardening pass: deny-by-default authorization, rate limits, body limits, and the
/// response headers a passive scanner looks for.
/// </summary>
public static class ApiHardening
{
    /// <summary>Rate-limit policy for credential endpoints.</summary>
    public const string AuthPolicy = "auth";

    /// <summary>Rate-limit policy for everything else.</summary>
    public const string GlobalPolicy = "global";

    /// <summary>Largest request body accepted on any endpoint, bytes.</summary>
    /// <remarks>
    /// 64 KB. Kestrel's default is 30 MB, which is a sensible default for an application that
    /// accepts file uploads and absurd for one whose largest legitimate body is a quote of at
    /// most 1,000 characters. The limit is set to what this API actually needs — a limit chosen
    /// for the workload rather than inherited from the framework.
    /// </remarks>
    public const long MaxRequestBodyBytes = 64 * 1024;

    public static IServiceCollection AddApiHardening(this IServiceCollection services)
    {
        // -----------------------------------------------------------------------------------
        // DENY BY DEFAULT.
        //
        // This is the most important line in the file, and it fixes a CLASS of bug rather than
        // an instance. Before it, authorization was opt-in: an endpoint was public unless
        // somebody remembered `.RequireAuthorization()`. Two groups had forgotten —
        // `/api/cache`, which can disable caching and reset counters, and `/upstream`, which can
        // trip the circuit breaker and inject faults — and both were reachable unauthenticated.
        //
        // With a fallback policy the default inverts. A new endpoint is protected unless it
        // explicitly says `.AllowAnonymous()`, so the failure mode of forgetting becomes a 401
        // during development rather than an open door in production.
        // -----------------------------------------------------------------------------------
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        // -----------------------------------------------------------------------------------
        // Kestrel body limit, applied globally.
        // -----------------------------------------------------------------------------------
        services.Configure<FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = MaxRequestBodyBytes;
            o.ValueLengthLimit = (int)MaxRequestBodyBytes;
        });

        // -----------------------------------------------------------------------------------
        // RATE LIMITS.
        //
        // Two policies, because the two paths are attacked differently.
        //
        // `auth` is tight — 5 attempts per minute per client. Login is where credential stuffing
        // lands, and BCrypt makes each attempt expensive for the SERVER as well as the attacker,
        // so an unthrottled login endpoint is simultaneously a brute-force surface and a cheap
        // denial-of-service. The limit is per-partition, and the partition is the caller.
        //
        // `global` is loose — 100 per minute — because it exists to stop a runaway client, not
        // to police normal use. Set it near real traffic and it becomes an outage generator.
        //
        // QueueLimit is ZERO on purpose. Queueing a rejected request holds a connection and a
        // thread on the server's side, which is the resource the limiter is protecting; refusing
        // immediately with 429 is both cheaper and more honest to the caller.
        // -----------------------------------------------------------------------------------
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(AuthPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            options.AddPolicy(GlobalPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            // Tell the caller when to come back. Without it a well-behaved client retries
            // immediately and a badly-behaved one is indistinguishable from it.
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
    /// The authenticated subject when there is one, the remote address otherwise.
    ///
    /// Partitioning by IP alone is wrong in both directions: every user behind one corporate NAT
    /// shares a bucket and throttles each other, while an attacker with a /64 of IPv6 has
    /// effectively unlimited buckets. Using the identity where one exists means an authenticated
    /// abuser cannot escape their limit by changing address, and only the unauthenticated paths
    /// fall back to the weaker signal.
    ///
    /// `X-Forwarded-For` is deliberately NOT consulted. It is caller-supplied unless a trusted
    /// proxy has overwritten it, and trusting it here would let anyone pick their own partition —
    /// turning the rate limiter off with a header.
    /// </remarks>
    private static string PartitionKey(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
            ? $"user:{context.User.Identity.Name ?? context.User.FindFirst("sub")?.Value ?? "unknown"}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    /// <summary>
    /// Response headers a passive scanner checks for, plus removal of the ones that leak.
    /// </summary>
    /// <remarks>
    /// Registered EARLY in the pipeline so the headers are present on error responses too — a
    /// 500 produced by an exception handler is exactly the response an attacker is most
    /// interested in, and headers added late are missing from precisely those.
    /// </remarks>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            // Stops a browser second-guessing Content-Type. Without it a JSON response whose
            // body happens to start with HTML can be sniffed and rendered as HTML, which turns a
            // stored string into stored XSS.
            headers["X-Content-Type-Options"] = "nosniff";

            // Clickjacking. A JSON API has nothing worth framing, so the strictest value is free.
            headers["X-Frame-Options"] = "DENY";

            // The modern equivalent of the above, and broader. `default-src 'none'` is correct
            // for an API that returns no HTML, no scripts and no styles: it says this response
            // should never load anything at all.
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

            // Do not leak the full URL - which can contain resource ids - to a third-party site
            // the user navigates to next.
            headers["Referrer-Policy"] = "no-referrer";

            // Disable browser features this API never uses, so a hijacked response cannot ask.
            headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";

            // ------------------------------------------------------------------------------
            // Site isolation. Added because the ZAP baseline flagged its absence — this set was
            // missing from the first pass, and the scanner is what caught it.
            //
            // These are the Spectre-era headers. A speculative-execution side channel lets a
            // malicious page read memory in its own process, so the defence is to ensure this
            // API's responses never SHARE a process with an attacker's page.
            //
            // `Cross-Origin-Resource-Policy: same-origin` is the one that matters for a JSON
            // API: it tells the browser to refuse to hand this response to a cross-origin
            // document at all, so a hostile page cannot pull an authenticated response into its
            // own address space to read it speculatively.
            // ------------------------------------------------------------------------------
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Embedder-Policy"] = "require-corp";

            // ------------------------------------------------------------------------------
            // Remove the banner.
            //
            // `Server: Kestrel` is free reconnaissance: it names the stack, which narrows the
            // set of CVEs worth trying. Removing it is not a security control - anyone
            // determined will fingerprint the behaviour instead - but it raises the cost of the
            // cheap, automated end of the attack spectrum, which is most of it.
            // ------------------------------------------------------------------------------
            headers.Remove("Server");
            headers.Remove("X-Powered-By");

            await next();
        });

    /// <summary>
    /// A response header naming the API version that served the request.
    /// </summary>
    /// <remarks>
    /// Versioning is in the URL (<c>/api/v1/...</c>) because a path is visible in a log, a
    /// browser bar and a cache key, whereas a header version is invisible in all three and gets
    /// lost by intermediaries. This header is the confirmation, not the mechanism: it lets a
    /// client assert which version actually answered, which matters during a migration when both
    /// are live.
    /// </remarks>
    public static IApplicationBuilder UseApiVersionHeader(this IApplicationBuilder app, string version) =>
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Api-Version"] =
                version.ToString(CultureInfo.InvariantCulture);
            await next();
        });
}
