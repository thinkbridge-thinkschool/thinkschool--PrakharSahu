using QuotesApi.Options;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Context;
using DotNetEnv;
using QuotesApi.Extensions;
using QuotesApi.Endpoints;
using QuotesApi.Data;
using QuotesApi.Models;
using Microsoft.EntityFrameworkCore;

Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration));
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("QuotesApi"))
    .WithTracing(t =>
    {
        t.AddSource("QuotesApi.Custom")
            .AddAspNetCoreInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddHttpClientInstrumentation();

        // Only export over OTLP when a collector endpoint is configured.
        // Left batched rather than Simple so a slow or absent collector never
        // blocks a request on the hot path.
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            t.AddOtlpExporter();
        }
    });

if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    otel.UseAzureMonitor(options => options.ConnectionString = appInsightsConnectionString);
}

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

builder.Services.AddHealthChecks();
var app = builder.Build();

app.Logger.LogInformation(
    "Startup: environment={Environment} azureMonitor={AzureMonitor} otlpExporter={Otlp}",
    app.Environment.EnvironmentName,
    string.IsNullOrWhiteSpace(appInsightsConnectionString) ? "disabled" : "enabled",
    string.IsNullOrWhiteSpace(otlpEndpoint) ? "disabled" : "enabled");

app.MapHealthChecks("/health");

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
    db.Database.EnsureCreated();

    if (!db.Users.Any())
    {
        // Seed credentials come from configuration rather than source. Locally
        // they are supplied by appsettings.Development.json; in Azure set
        // Seed__AdminEmail / Seed__AdminPassword on the container app.
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

    // INJECT TEST DATA
    var adminUser = db.Users.FirstOrDefault();
    if (adminUser != null && db.Quotes.Count() < 500)
    {
        for (int i = 0; i < 500; i++)
        {
            var quoteResult = Quote.Create($"Author {i}", $"Load test quote {i}", DateTime.UtcNow, adminUser.Id);
            if (quoteResult.IsSuccess)
            {
                db.Quotes.Add(quoteResult.Value!);
            }
        }
        db.SaveChanges();
        app.Logger.LogInformation("Seeded 500 dummy quotes for load testing.");
    }

    try
    {
        // --- DAY 11 PIECE 2: ADD THE INDEX BACK ---
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Quotes_UserId ON Quotes(UserId);");
        app.Logger.LogInformation("Added IX_Quotes_UserId index to optimize performance.");

        // Print the optimized execution plan proving an index seek is happening
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT UserId, COUNT(Id) FROM Quotes WHERE UserId = 1";
        db.Database.OpenConnection();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            app.Logger.LogInformation("DAY 11 OPTIMIZED PLAN: {Plan}", reader.GetString(3)); 
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to create index or retrieve execution plan.");
    }
}

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapQuoteEndpoints();

app.Run();

public partial class Program { }