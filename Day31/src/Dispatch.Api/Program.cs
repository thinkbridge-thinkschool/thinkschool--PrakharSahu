using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Dispatch.Api.Endpoints;
using Dispatch.Api.Messaging;
using Dispatch.Api.Security;
using Dispatch.Billing.Infrastructure;
using Dispatch.Billing.Infrastructure.Persistence;
using Dispatch.Scheduling.Infrastructure;
using Dispatch.Scheduling.Infrastructure.Persistence;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Infrastructure;
using Dispatch.WorkManagement.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// ==============================================================================================
// The composition root, and the only place in the solution that knows all three modules exist.
//
// Read the list below as the system's architecture: three modules, one shared kernel, one
// transport. Adding a fourth module is one line here and one endpoint file -- not a search
// through Program.cs for where its various pieces need to be threaded in.
// ==============================================================================================

builder.Services.AddSingleton<IClock, SystemClock>();

// The transport. Swapping this for a real broker is the only change needed to split a module
// out; no module has ever been allowed to know which implementation it was talking to.
builder.Services.AddSingleton<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();

// =================================================================================================
// Where the data lives. The HOST decides this, not the modules.
//
// Each module registered its own DbContext behind an AddXPersistence(connectionString) method, so
// the module knows it needs a database and knows nothing about which one. That is what lets the
// same three modules run against Azure SQL here and against fakes in the application tests.
//
// One connection string, three schemas. The modules cannot see each other's tables because the
// types are not reachable from their contexts -- the isolation is a compile-time fact, not a
// permissions grant -- while operationally this is still one database with one backup.
// =================================================================================================
var connectionString = builder.Configuration.GetConnectionString("Dispatch")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Dispatch is not configured. Set ConnectionStrings__Dispatch. " +
        "There is deliberately no fallback: an app that silently starts on a database nobody " +
        "meant to use is worse than one that refuses to start.");

builder.Services.AddWorkManagementPersistence(connectionString);
builder.Services.AddSchedulingPersistence(connectionString);
builder.Services.AddBillingPersistence(connectionString);

builder.Services.AddWorkManagement();
builder.Services.AddScheduling();
builder.Services.AddBilling();

builder.Services.AddProblemDetails();

// Day 31 security re-check. Rate limiting and the body cap; headers are applied below, and
// authentication is deliberately still absent -- see ApiHardening for why.
builder.Services.AddApiHardening();

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Kestrel's own limit, separate from any model-binding limit, and the one that stops a large
    // body being READ at all. 30 MB by default; this API's largest legitimate request is a work
    // order with a 500-character summary.
    kestrel.Limits.MaxRequestBodySize = ApiHardening.MaxRequestBodyBytes;

    // Do not advertise the stack in every response.
    kestrel.AddServerHeader = false;
});

// Enums travel as strings, in and out.
//
// Without this, System.Text.Json binds enums by their NUMERIC value, so {"priority":"High"}
// is a 400 and {"priority":2} is accepted -- which means the API's contract is a set of
// magic numbers that silently change meaning the day somebody inserts a new enum member in
// the middle. The smoke test caught exactly that: every request after triage failed, because
// triage itself had quietly 400'd.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// ---------------------------------------------------------------------------------------------
// Migrate on startup, OPT-IN and off by default.
//
// Deliberately not the default, for two reasons that only show up in production:
//
//   * several replicas starting together would race on the same migration, and EF's migration
//     lock turns that into a startup stall rather than a corruption -- but only if every replica
//     is running the same version, which during a rolling deploy it is not;
//   * a destructive migration would be applied by whichever pod happened to boot first, with no
//     human reviewing it.
//
// It exists because the end-to-end test starts this binary against a database that was created
// seconds earlier. That is exactly the case where auto-migration is right: one process, a fresh
// database, nothing to lose. Everywhere else it is a deployment step -- build-plan day 9.
if (builder.Configuration.GetValue<bool>("DISPATCH_MIGRATE_ON_STARTUP"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<WorkManagementDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<SchedulingDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();
}

// Order matters. Security headers go on FIRST so they are present on every response, including
// the 404s, 429s and 500s produced further down the pipeline.
app.UseSecurityHeaders();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// HTTP lives in the host, not in the modules.
//
// The tradeoff, stated rather than hidden: this means adding an endpoint touches the host, which
// is a small dent in module autonomy. The alternative -- a Presentation project per module with
// a FrameworkReference to ASP.NET Core -- buys that autonomy back at the cost of three more
// projects and a web framework dependency inside every module. At three modules that is not
// worth it. At ten it will be, and moving these files then is mechanical.
app.MapWorkOrderEndpoints();
app.MapBillingEndpoints();

app.Run();

/// <summary>Exposed so the test host can boot the real composition root.</summary>
public partial class Program;
