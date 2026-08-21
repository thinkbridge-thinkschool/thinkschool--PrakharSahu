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
using Microsoft.EntityFrameworkCore; // Required for EF Core extensions like ExecuteSqlRaw

// Local development convenience only. Env.Load() no-ops when there is no .env
// file, which is the case inside the deployed container -- there, Azure
// Container Apps supplies the real environment variables.
Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration));
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddInfrastructure(builder.Configuration);

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

    // INJECT TEST DATA, DROP INDEX, AND PRINT EXECUTION PLAN
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
        // Force the missing index for the Day 11 requirement
        db.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_Quotes_UserId;");
        app.Logger.LogWarning("Dropped IX_Quotes_UserId index to simulate poor performance.");

        // Print the execution plan proving a full table scan is happening
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT * FROM Quotes WHERE UserId = 1";
        db.Database.OpenConnection();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            app.Logger.LogCritical("DAY 11 EXECUTION PLAN: {Plan}", reader.GetString(3)); 
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to drop index or retrieve execution plan.");
    }
}

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapQuoteEndpoints();

app.Run();

public partial class Program { }