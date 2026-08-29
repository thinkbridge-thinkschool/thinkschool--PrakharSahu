using Microsoft.Extensions.Http.Resilience;
using Polly;
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

Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration));
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

// Day 17: requires an Entra app-only token on /api/*, proving the request came through the
// Static Web App's BFF. Registers nothing unless CallerIdentity:TenantId and
// CallerIdentity:Audience are both set, so local runs and the Week-1 tests are unaffected.
builder.Services.AddCallerIdentity(builder.Configuration);


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
builder.Services.AddHttpClient("ExternalService")
    .AddResilienceHandler("default", b =>
    {
        b.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        })
        .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 2
        })
        .AddTimeout(TimeSpan.FromSeconds(10));
    });

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
    if (!db.Quotes.Any())
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
app.UseCallerIdentity();

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapQuoteEndpoints();
app.MapWhoAmI();

app.MapGet("/test-retry", async (IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("ExternalService");
    var response = await client.GetAsync("https://httpstat.us/500");
    return Results.Content($"Status: {response.StatusCode}");
});

app.Run();

public partial class Program { }
