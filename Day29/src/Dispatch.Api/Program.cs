using System.Text.Json.Serialization;
using Dispatch.Api.Endpoints;
using Dispatch.Api.Messaging;
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
