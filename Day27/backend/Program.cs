using QuotesApi.Options;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Context;
using DotNetEnv;
using QuotesApi.Extensions;
using QuotesApi.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using QuotesApi.Security;
using QuotesApi.Data;
using QuotesApi.Models;

Env.Load();

var builder = WebApplication.CreateBuilder(args); // creates the app builder

// =============================================================================================
// Day 26: which ROLE is this process?
//
// The exercise asks for a trace spanning "API -> worker -> DB". Until today both halves ran in
// one process, and a trace of that is not a distributed trace — Activity flows on the async
// context, so every span is already correctly parented with nothing wired. It would demonstrate
// nothing, because nothing had to cross a boundary.
//
// So the same binary now runs as one of two roles:
//
//   api     serves HTTP, writes quotes and outbox rows. Publishes nothing, consumes nothing.
//   worker  no endpoints. Runs the outbox relay and the subscription consumers.
//
// One binary rather than two projects, because the alternative duplicates the entire DI graph
// and the two copies drift. The role is a runtime decision; everything else is shared.
//
// This matters for the DELIVERABLE, not just for tidiness: App Insights groups spans by
// `cloud_RoleName` when it draws the application map and the end-to-end transaction view. Two
// roles means two boxes with an arrow between them. One role would still stitch the trace
// correctly and render it as a single box — technically right and visually useless.
// =============================================================================================
var serviceRole = (builder.Configuration["SERVICE_ROLE"] ?? "all").Trim().ToLowerInvariant();

var isWorkerRole = serviceRole is "worker" or "all";
var isApiRole = serviceRole is "api" or "all";

if (serviceRole is not ("api" or "worker" or "all"))
{
    // Fail loudly. A typo silently falling back to "all" would run both roles in one process and
    // produce a trace that looks right and proves nothing — the exact failure this codebase has
    // spent several days learning to distrust.
    throw new InvalidOperationException(
        $"SERVICE_ROLE was '{serviceRole}'. Expected 'api', 'worker' or 'all'.");
}

var roleName = serviceRole switch
{
    "api" => QuotesApi.Observability.Telemetry.ApiRoleName,
    "worker" => QuotesApi.Observability.Telemetry.WorkerRoleName,
    _ => "quotes-all-in-one"
};

builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration));
builder.Configuration.AddEnvironmentVariables(); // this allows config values to come from env

// ---------------------------------------------------------------------------------------------
// Day 26: the API role turns its own background workers OFF.
//
// Done through configuration rather than by editing AddMessaging/AddOutbox, because those
// extension methods already have exactly the switches needed — `Outbox:Enabled` and the presence
// of Service Bus configuration — and they are covered by the Day 19-22 test suites. Reaching in
// to add a role parameter would change three signatures and the tests that call them, to express
// something the existing switches already say.
//
// Added AFTER AddEnvironmentVariables so it overrides .env, which is the point: .env carries the
// broker settings both roles read, and the API role has to opt out of them specifically.
//
// The effect is that in the api role AddMessaging sees no broker and registers NoOpEventPublisher
// with no SubscriptionWorker, and the outbox relay returns immediately. The API still WRITES
// outbox rows — that is IOutboxWriter, which needs no broker at all.
// ---------------------------------------------------------------------------------------------
if (!isWorkerRole)
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Outbox:Enabled"] = "false",
        ["ServiceBus:FullyQualifiedNamespace"] = string.Empty,
        ["ServiceBus:ConnectionString"] = string.Empty
    });
}

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment); // db, repo, auth and other core services gets registered(Infrastructure registration)

// Day 17: requires an Entra app-only token on /api/*, proving the request came through the
// Static Web App's BFF. Registers nothing unless CallerIdentity:TenantId and
// CallerIdentity:Audience are both set, so local runs and the Week-1 tests are unaffected.
builder.Services.AddCallerIdentity(builder.Configuration); // check the caller id coming through BFF/Entra setup

// Day 18: the job queue, its BackgroundService worker, and the IHostedService that fails
// startup if the pipeline is misconfigured. AddJobAwareShutdownTimeout must be called too —
// the default 5s ShutdownTimeout is shorter than the grace period a running job is given, so
// without it the host kills the process mid-job and the grace period accomplishes nothing.
builder.Services.AddBackgroundJobs(builder.Configuration);
builder.Services.AddJobAwareShutdownTimeout(builder.Configuration);

// Day 19: publish quote events to a Service Bus topic and drain its two subscriptions with
// competing consumers. Registers a no-op publisher when ServiceBus:ConnectionString is absent,
// so the API still runs on a machine with no broker.
builder.Services.AddMessaging(builder.Configuration);

// Day 20: the transactional outbox. Quote creation now writes the event as a row in the same
// transaction as the quote, and this relay publishes it afterwards — so a crash between the
// commit and the publish loses nothing.
builder.Services.AddOutbox(builder.Configuration);

// Day 21: HybridCache (L1 in-memory + optional L2 Redis) over the hot read, with the counters
// that make hit rate and DB load measurable. Runs L1-only when no Redis is configured.
builder.Services.AddQuoteCaching(builder.Configuration);

// Day 22: the Polly pipeline around the outbound dependency -- bulkhead, total timeout,
// idempotent-only retry, circuit breaker, attempt timeout -- plus the state provider and event
// log that make the breaker's closed -> open -> half-open -> closed cycle observable.
//
// This replaces the Week-1 AddHttpClient("ExternalService") handler that used to sit further
// down this file. That one retried every request regardless of whether repeating it was safe,
// had no concurrency limit at all, and reported nothing about the breaker, so there was no way
// to tell an open circuit from a dependency that had merely gone quiet.
builder.Services.AddUpstreamResilience(builder.Configuration); // polly based resilience pipeline


var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

var otel = builder.Services.AddOpenTelemetry() // registers openTelemetry

    // `AddService` sets the OpenTelemetry resource attribute `service.name`, which the Azure
    // Monitor exporter maps onto `cloud_RoleName`. That column is what every KQL query in
    // Day26/kql groups by, and what App Insights uses to decide how many boxes to draw on the
    // application map. The instance id separates two processes of the SAME role, which is how a
    // "one replica is slow" problem stays visible after horizontal scaling.
    .ConfigureResource(r => r.AddService(
        serviceName: roleName,
        serviceVersion: "day26",
        serviceInstanceId: $"{Environment.MachineName}:{Environment.ProcessId}"))

    .WithTracing(t =>
    {
        // Every ActivitySource the application raises spans through. A source that is not
        // registered here emits NOTHING — silently, with no warning and no error — and the
        // resulting gap in a trace is indistinguishable from a broken propagation. The list is
        // derived from the sources themselves in Telemetry.SourceNames so it cannot drift.
        foreach (var source in QuotesApi.Observability.Telemetry.SourceNames)
        {
            t.AddSource(source);
        }

        t.AddSource("QuotesApi.Custom")     // inherited from Day 11; kept so older spans still flow
            .AddAspNetCoreInstrumentation()
            // The DB tier of "API -> worker -> DB". Left at its defaults, and the default that
            // matters is the one NOT changed: this instrumentation can be told to attach query
            // PARAMETER VALUES to each span, and it must not be.
            //
            // Parameter values are precisely where user data lives — an email address, a token,
            // whatever arrived in the request body. Attaching them exports that to a telemetry
            // store queryable by anyone with Reader on the workspace, retained for 30 days, and
            // outside every control the application database has. The SQL text alone is a
            // parameterised template and carries none of it.
            //
            // (The option is documented as `SetDbQueryParameters` but is not public in the
            // 1.17.0-beta.1 assembly, so it cannot be set explicitly here even to say "false".
            // Recorded rather than assumed, because a future upgrade may expose it.)
            .AddEntityFrameworkCoreInstrumentation()
            .AddHttpClientInstrumentation();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            t.AddOtlpExporter();
        }
    })

    // -----------------------------------------------------------------------------------------
    // Metrics, which Day 11 never wired and which traces cannot replace.
    //
    // A trace answers "what happened to THIS request". A metric answers "what is happening in
    // general". Computing a p99 by scanning traces is expensive, and wrong the moment sampling
    // is enabled anywhere — the survivors of a sampler are not a representative latency sample.
    //
    // The KQL in this project reads p50/p99 from `requests`, which is trace data, because at
    // 100% sampling on a dev workload that is exact and simpler to demonstrate. The instruments
    // below are what that query would have to become at production volume.
    // -----------------------------------------------------------------------------------------
    .WithMetrics(m =>
    {
        m.AddMeter(QuotesApi.Observability.Telemetry.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            m.AddOtlpExporter();
        }
    });

if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    otel.UseAzureMonitor(options => options.ConnectionString = appInsightsConnectionString);
}

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

// ---------------------------------------------------------------------------------------------
// Day 27: the security pass.
//
// Deny-by-default authorization, two rate-limit policies, and form/body size caps. See
// Security/ApiHardening.cs - the fallback policy is the line that matters, because it fixes a
// class of bug rather than the two instances of it that were found.
// ---------------------------------------------------------------------------------------------
builder.Services.AddApiHardening();

// Kestrel's own limit, which is separate from the form limits and is the one that stops a large
// body being READ. 30 MB by default; this API's largest legitimate request is a 1 KB quote.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = ApiHardening.MaxRequestBodyBytes;

    // Do not advertise the stack in every response. See UseSecurityHeaders for why this is a
    // cost-raising measure rather than a control.
    kestrel.AddServerHeader = false;
});

// OpenAPI, describing the surface as it actually is - versioned, and bearer-authenticated.
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new OpenApiInfo
        {
            Title = "Quotes API",
            Version = ApiVersioning.Version,
            Description = "Quotes API. All endpoints require a bearer token unless marked otherwise."
        };

        // Declaring the scheme is what makes the document usable rather than merely accurate: a
        // generated client knows to send Authorization, and a reviewer can see at a glance that
        // the API has exactly one way in.
        //
        // `OpenApiSecurityScheme` (concrete) rather than `IOpenApiSecurityScheme` (the dictionary's
        // value type). Microsoft.OpenApi v2 exposes the interface as read-only, so the properties
        // can only be set on the implementation.
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "JWT issued by POST /api/v1/auth/login."
        };

        return Task.CompletedTask;
    });
});

builder.Services.AddHealthChecks();

var app = builder.Build();

app.Logger.LogInformation(
    "Startup: environment={Environment} azureMonitor={AzureMonitor} otlpExporter={Otlp}",
    app.Environment.EnvironmentName,
    string.IsNullOrWhiteSpace(appInsightsConnectionString) ? "disabled" : "enabled",
    string.IsNullOrWhiteSpace(otlpEndpoint) ? "disabled" : "enabled");

// ---------------------------------------------------------------------------------------------
// Day 27 middleware, and the ORDER is deliberate.
//
// Security headers go on FIRST so they are present on every response, including the 401s, 429s
// and 500s produced further down. Headers added late are missing from exactly the responses an
// attacker is most interested in.
// ---------------------------------------------------------------------------------------------
app.UseSecurityHeaders();
app.UseApiVersionHeader(ApiVersioning.Version);
app.UseRateLimiter();

// /health is deliberately anonymous and unversioned. A probe that has to authenticate cannot
// distinguish "the app is down" from "the token is wrong", and orchestrators do not carry one.
app.MapHealthChecks("/health").AllowAnonymous();

// The OpenAPI document itself is dev-only. Publishing a complete map of the surface, including
// the endpoints an attacker has not discovered yet, is free reconnaissance in production - and
// the people who need the document have access to a non-production environment.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
}
// middleware (req -> this middleware -> next middleware)
app.Use(async (ctx, next) =>
{
    using (LogContext.PushProperty("TraceId", ctx.TraceIdentifier))
    {
        await next(ctx);
    }
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // -----------------------------------------------------------------------------------------
    // Day 26: exactly ONE role creates the schema.
    //
    // Splitting one process into two introduced a startup race that a single process could not
    // have. Both roles called EnsureCreated() against the same empty file at the same moment,
    // both decided the tables were missing, and both issued CREATE TABLE. One won:
    //
    //   Microsoft.Data.Sqlite.SqliteException (0x80004005):
    //   SQLite Error 1: 'table "OutboxMessages" already exists'.
    //
    // EnsureCreated is not atomic and makes no attempt to be. The check and the create are
    // separate statements with a window between them, and two processes starting together land
    // in it reliably rather than occasionally.
    //
    // The fix is ownership rather than locking: schema creation belongs to the api role, and the
    // worker waits for the result. That is also how this works anywhere real — migrations are run
    // once by one owner, not by every replica racing at boot.
    // -----------------------------------------------------------------------------------------
    if (isApiRole)
    {
        db.Database.EnsureCreated();
    }
    else
    {
        // Poll rather than assume. A worker started before the API would otherwise fail its
        // first sweep with "no such table", log an error, and recover only on the next poll —
        // noise that looks like a real fault in the logs the exercise asks people to read.
        var schemaDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < schemaDeadline)
        {
            try
            {
                _ = db.OutboxMessages.Any();
                break;
            }
            catch (Exception)
            {
                app.Logger.LogInformation("Worker: waiting for the api role to create the schema...");
                Thread.Sleep(2000);
            }
        }
    }

    // -----------------------------------------------------------------------------------------
    // Day 26: WAL, because two processes now share one SQLite file.
    //
    // Until today the API and its workers were one process, so SQLite's default rollback journal
    // was fine — one writer, one file, no contention. Splitting into an api role and a worker
    // role means two OS processes writing the same file: the API inserting quotes and outbox
    // rows, the relay updating those rows as it publishes them.
    //
    // Under the default journal mode a writer takes an exclusive lock over the whole database,
    // so the two processes collide and one gets:
    //
    //   Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 5: 'database is locked'
    //
    // Write-Ahead Logging lets readers proceed while a writer is active and makes lock waits
    // resolvable rather than immediate failures. `busy_timeout` then covers the writer-vs-writer
    // case: instead of failing instantly, a blocked write waits up to five seconds, which is far
    // longer than any operation here takes.
    //
    // WAL is persistent — it is a property of the database file, not the connection — so setting
    // it once at startup is enough. It is set by BOTH roles anyway, because whichever process
    // starts first should establish it, and re-applying it is a no-op.
    //
    // This is a SQLite limitation, not a design choice worth keeping. A real deployment would use
    // the Azure SQL database Day 25 provisioned, where cross-process concurrency is the baseline
    // assumption rather than something to configure around.
    // -----------------------------------------------------------------------------------------
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");

    // Seeding is the api role's job too, for the same reason as the schema: two processes
    // both finding an empty table and both inserting produces duplicate users.
    if (isApiRole && !db.Users.Any())
    {
        var seedEmail = app.Configuration["Seed:AdminEmail"];
        var seedPassword = app.Configuration["Seed:AdminPassword"];

        if (!string.IsNullOrWhiteSpace(seedEmail) && !string.IsNullOrWhiteSpace(seedPassword))
        {
            db.Users.Add(new User
            {
                Email = seedEmail,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(seedPassword)
            });
            db.SaveChanges();
            app.Logger.LogInformation("Seeded initial user {Email}.", seedEmail);
        }
        else
        {
            app.Logger.LogWarning(
                "User table is empty and Seed:AdminEmail / Seed:AdminPassword are not configured, " +
                "so no user was seeded. Login will return 401 until a user exists.");
        }
    }

    // Day 17: a handful of quotes so the deployed app is never staring at an empty list.
    //
    // This matters more here than it would elsewhere. The database is SQLite inside the
    // container, so it does not survive a revision restart — every deploy, scale-to-zero or
    // platform-initiated restart returns an empty table, and the live URL then renders its
    // empty state. That is the app behaving correctly, but to anyone opening the link it
    // looks like a broken deployment.
    //
    // UserId 0 deliberately belongs to nobody. IsOwnerHandler compares the quote's UserId to
    // the caller's `sub`, and no real user is ever id 0, so these are readable by everyone and
    // deletable by no one. Seed content cannot be removed by the first person to sign in.
    //
    // Only runs when the table is empty, so it never fights real data.
    if (isApiRole && !db.Quotes.Any())
    {
        // Text and author both go through TextRules, which is an allow-list: letters, digits,
        // whitespace and . , ' " - ? ( ) only. No exclamation marks, semicolons or em dashes.
        var seedQuotes = new[]
        {
            ("Ada Lovelace", "That brain of mine is something more than merely mortal, as time will show."),
            ("Grace Hopper", "The most damaging phrase in the language is, we have always done it this way."),
            ("Alan Turing", "We can only see a short distance ahead, but we can see plenty there that needs to be done."),
            ("Edsger W. Dijkstra", "Simplicity is a great virtue but it requires hard work to achieve it."),
            ("Barbara Liskov", "Everything is best for something and worst for something else.")
        };

        var clock = scope.ServiceProvider.GetRequiredService<QuotesApi.Services.IClock>();
        var seeded = 0;

        foreach (var (author, text) in seedQuotes)
        {
            var result = Quote.Create(author, text, clock.UtcNow, userId: 0);
            if (result.IsSuccess)
            {
                db.Quotes.Add(result.Value!);
                seeded++;
            }
            else
            {
                // A seed quote that fails validation is a bug in the seed data, not a runtime
                // condition. Log it rather than throwing, so a typo cannot stop the API booting.
                app.Logger.LogWarning(
                    "Seed quote by {Author} was rejected: {Reason}", author, result.Error);
            }
        }

        db.SaveChanges();
        app.Logger.LogInformation("Seeded {Count} demo quotes.", seeded);
    }
}

// Ahead of UseAuthentication, so a request with no service credential is turned away before
// the API spends any effort deciding which user it belongs to.
app.UseCallerIdentity(); // Runs your caller identity middleware.

app.UseAuthentication(); // handles authentication
app.UseAuthorization(); // handles authorization

// ---------------------------------------------------------------------------------------------
// Day 26: only the api role serves the API.
//
// The worker role keeps /health, mapped further up, and nothing else. That is not cosmetic: a
// worker that also exposes endpoints would receive requests, produce `requests` telemetry under
// cloud_RoleName 'quotes-worker', and quietly corrupt every per-endpoint latency query in
// Day26/kql — the p99 for an endpoint would be an average across two roles that do different
// amounts of work.
// ---------------------------------------------------------------------------------------------
if (isApiRole)
{
    // -----------------------------------------------------------------------------------------
    // Day 27: every endpoint hangs off ONE versioned, rate-limited group.
    //
    // Two properties come from the group rather than from each endpoint remembering to ask:
    //
    //   the /api/v1 prefix   so a v2 has somewhere to exist without evicting v1
    //   the global limiter   so a new endpoint is throttled by construction
    //
    // Combined with the fallback authorization policy in AddApiHardening, the default for
    // anything added later is versioned, rate-limited and authenticated. Opting out is possible
    // and has to be written down, which is the right way round — the previous arrangement made
    // "public and unlimited" the thing you got by forgetting.
    // -----------------------------------------------------------------------------------------
    var v1 = app.MapGroup(ApiVersioning.Prefix)
                .RequireRateLimiting(ApiHardening.GlobalPolicy);

    v1.MapAuthEndpoints();
    v1.MapQuoteEndpoints();
    v1.MapWhoAmI();
    v1.MapJobEndpoints();
    v1.MapMessagingEndpoints();
    v1.MapOutboxEndpoints();

    // -----------------------------------------------------------------------------------------
    // DEV-ONLY. These do not exist in Production.
    //
    // /cache exposes POST /mode and POST /reset; /resilience exposes POST /breaker/{action} and
    // fault injection; /upstream is the fake dependency the Polly pipeline calls. All three are
    // instrumentation for demonstrating Days 21 and 22, and all three are levers over the
    // application's behaviour.
    //
    // Before today they were mapped unconditionally AND unauthenticated, so anyone who could
    // reach the service could disable its cache or hold its circuit breaker open. The fallback
    // policy now closes the authentication hole, but authenticating a debug lever is the wrong
    // fix: the right one is that it is not present at all in an environment that did not ask
    // for it. An endpoint that does not exist cannot be misconfigured.
    // -----------------------------------------------------------------------------------------
    if (app.Environment.IsDevelopment())
    {
        v1.MapCacheEndpoints();
        app.MapResilienceEndpoints();
        app.Logger.LogWarning(
            "Development environment: cache-control, fault-injection and fake-upstream endpoints "
            + "are mapped. These are NOT mapped in Production.");
    }
}
else
{
    app.Logger.LogInformation(
        "Worker role: no HTTP endpoints mapped beyond /health. Running the outbox relay and "
        + "subscription consumers only.");
}

app.Run();

public partial class Program { } // This makes Program accessible to other code, especially useful for integration testing.
