using System.Text.Json.Serialization;
using Dispatch.Api.Endpoints;
using Dispatch.Api.Messaging;
using Dispatch.Billing.Infrastructure;
using Dispatch.Scheduling.Infrastructure;
using Dispatch.SharedKernel;
using Dispatch.WorkManagement.Infrastructure;

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
