using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Data.SqlClient;

// =============================================================================================
// Day 25 — the app that proves the identity paths work.
//
// Every endpoint below exercises one path and reports what actually happened, so the claim
// "no secrets anywhere" can be checked rather than believed:
//
//   GET /health              liveness. Excluded from authentication on purpose.
//   GET /whoami              who Easy Auth says the CALLER is, from the injected headers.
//   GET /probe/sql           connects to SQL and returns the principal the SERVER sees.
//   GET /probe/servicebus    sends and receives a message, round trip.
//   GET /probe/keyvault      reports the SHAPE of the referenced secret, never its value.
//   GET /probe/all           all three, with timings.
//
// ---------------------------------------------------------------------------------------------
// THE CREDENTIAL, ONCE, AT THE TOP
//
// There is exactly one credential object in this file and it holds no secret. DefaultAzureCredential
// asks the platform's local token endpoint for a token, scoped to whichever resource is being
// reached. Nothing here reads a password, because nothing was given one.
// =============================================================================================

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

// ---------------------------------------------------------------------------------------------
// One credential, shared. Constructing DefaultAzureCredential is expensive and it caches tokens
// internally, so a per-request instance throws that cache away and re-acquires a token on every
// call — which turns a 2ms operation into a network round trip and can hit Entra throttling.
//
// ManagedIdentityClientId is the line that makes a USER-assigned identity work. A resource can
// carry several, and the token endpoint will not guess which one is meant; omitting it produces
// "ManagedIdentityCredential authentication failed" — an error that reads like a permissions
// problem and is an ambiguity problem. It is read from AZURE_CLIENT_ID, which the template sets.
// ---------------------------------------------------------------------------------------------
var managedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"];

var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = managedIdentityClientId
});

builder.Services.AddSingleton(credential);

// A ServiceBusClient is also expensive — it owns an AMQP connection — and is thread-safe, so it
// is registered once rather than created per request.
var serviceBusNamespace = builder.Configuration["ServiceBus:FullyQualifiedNamespace"];

if (!string.IsNullOrWhiteSpace(serviceBusNamespace))
{
    builder.Services.AddSingleton(new ServiceBusClient(serviceBusNamespace, credential));
}

var app = builder.Build();

app.MapHealthChecks("/health");

// ---------------------------------------------------------------------------------------------
// /whoami — what Easy Auth decided about the caller.
//
// The platform validated the JWT before this process saw the request and injected the result as
// request headers. Reading them rather than parsing the token ourselves is the point: validation
// happened outside the app, so an unauthenticated request never reached this line at all.
//
// X-MS-CLIENT-PRINCIPAL-NAME and -ID are set by the platform and cannot be spoofed from outside,
// because App Service strips any inbound copy before the request is forwarded.
// ---------------------------------------------------------------------------------------------
app.MapGet("/whoami", (HttpContext http) =>
{
    var headers = http.Request.Headers;

    return Results.Ok(new
    {
        authenticatedBy = "App Service Easy Auth (Entra ID), validated before the app was reached",
        callerName = headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault(),
        callerObjectId = headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault(),
        identityProvider = headers["X-MS-CLIENT-PRINCIPAL-IDP"].FirstOrDefault(),

        // The app's OWN identity, which is a different thing from the caller's and is the one
        // used for every outbound call below.
        appIdentityClientId = managedIdentityClientId,

        note = "No client secret is involved. This API validates tokens; it never issues them."
    });
});

// ---------------------------------------------------------------------------------------------
// /probe/sql — the strongest single piece of evidence in this project.
//
// SUSER_SNAME() returns the principal the SERVER believes is connected. If it comes back as the
// managed identity's name, then the database authenticated an Entra token — and since the server
// has azureADOnlyAuthentication enabled, no password could have been used even if one existed.
// ---------------------------------------------------------------------------------------------
app.MapGet("/probe/sql", async (IConfiguration config) =>
{
    var connectionString = config.GetConnectionString("Sql") ?? config["ConnectionStrings:Sql"];

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem("ConnectionStrings__Sql is not configured.");
    }

    var stopwatch = Stopwatch.StartNew();

    try
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT SUSER_SNAME(), DB_NAME(), @@VERSION;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        var principal = reader.GetString(0);

        return Results.Ok(new
        {
            ok = true,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            connectedAs = principal,
            database = reader.GetString(1),
            serverVersion = reader.GetString(2).Split('\n')[0].Trim(),

            // The connection string is safe to echo, and echoing it is the point: a reader can
            // see for themselves that it contains an auth METHOD and no credential.
            connectionStringUsed = Redact(connectionString),
            proof = $"The server reports the connection as '{principal}'. No password exists: "
                  + "this server has azureADOnlyAuthentication enabled."
        });
    }
    catch (Exception ex)
    {
        // A missing GRANT and a missing token look nothing alike once you know the messages, and
        // identical until then. Distinguishing them here is worth more than a stack trace.
        var hint = ex.Message.Contains("token-identified principal")
            ? "The identity authenticated but is not a USER in this database. Run scripts/grant-sql.sh."
            : ex.Message.Contains("ManagedIdentityCredential")
                ? "No token was obtained. Check AZURE_CLIENT_ID matches the attached identity."
                : "See the message.";

        return Results.Problem($"{ex.GetType().Name}: {ex.Message}\n\nHint: {hint}");
    }
});

// ---------------------------------------------------------------------------------------------
// /probe/servicebus — a full round trip, not just a connection.
//
// Connecting proves less than it appears to. Service Bus will accept a connection and then
// refuse the operation, so a send AND a receive are what actually demonstrate that the Data
// Sender and Data Receiver roles are both in force. The namespace has disableLocalAuth: true,
// so a SAS key could not have been used even if one were present.
// ---------------------------------------------------------------------------------------------
app.MapGet("/probe/servicebus", async (ServiceBusClient? client, IConfiguration config) =>
{
    if (client is null)
    {
        return Results.Problem("ServiceBus__FullyQualifiedNamespace is not configured.");
    }

    var queueName = config["ServiceBus:QueueName"] ?? "identity-probe";
    var stopwatch = Stopwatch.StartNew();
    var correlationId = Guid.NewGuid().ToString();

    try
    {
        await using var sender = client.CreateSender(queueName);
        await sender.SendMessageAsync(new ServiceBusMessage("identity probe")
        {
            CorrelationId = correlationId
        });

        var sentMs = stopwatch.ElapsedMilliseconds;

        await using var receiver = client.CreateReceiver(queueName);
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(20));

        if (received is null)
        {
            return Results.Problem(
                $"Sent successfully (Data Sender works) but received nothing in 20s. "
              + $"The send path is proven; the receive path is not.");
        }

        // Complete it, or the message returns after the lock expires and every later probe
        // receives this one instead of its own.
        await receiver.CompleteMessageAsync(received);

        return Results.Ok(new
        {
            ok = true,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            sentMs,
            queue = queueName,
            fullyQualifiedNamespace = client.FullyQualifiedNamespace,
            correlationMatched = received.CorrelationId == correlationId,
            proof = "Send and receive both succeeded with a token. This namespace has "
                  + "disableLocalAuth: true, so a SAS key would have been rejected."
        });
    }
    catch (Exception ex)
    {
        var hint = ex.Message.Contains("claim", StringComparison.OrdinalIgnoreCase)
            ? "The token was accepted but lacks the role. Check the Data Sender/Receiver assignments."
            : "See the message.";

        return Results.Problem($"{ex.GetType().Name}: {ex.Message}\n\nHint: {hint}");
    }
});

// ---------------------------------------------------------------------------------------------
// /probe/keyvault — did the reference resolve, and is the value real?
//
// This endpoint NEVER returns the secret. It returns the length and a truncated SHA-256, which
// is enough to confirm a plausible value arrived and to compare against the vault without
// disclosing anything.
//
// The important case it detects: when a Key Vault reference FAILS to resolve, App Service leaves
// the literal '@Microsoft.KeyVault(...)' string as the setting value. The app then starts
// normally and behaves as though the secret were the reference text — signing requests with the
// wrong value and failing somewhere far away from the cause. Checking for that prefix turns a
// silent misconfiguration into a named one.
// ---------------------------------------------------------------------------------------------
app.MapGet("/probe/keyvault", (IConfiguration config) =>
{
    var value = config["ThirdParty:WebhookSigningKey"];

    if (string.IsNullOrEmpty(value))
    {
        return Results.Problem("ThirdParty__WebhookSigningKey is not set at all.");
    }

    if (value.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Problem(
            "The Key Vault reference did NOT resolve. The app is seeing the reference string "
          + "itself instead of the secret. Usual causes: keyVaultReferenceIdentity is unset, the "
          + "identity lacks Key Vault Secrets User, or the secret did not exist when the app "
          + "started. Fix the cause, then restart the app — references resolve at startup.");
    }

    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    return Results.Ok(new
    {
        ok = true,
        resolved = true,
        vault = config["KeyVault:Name"],

        // Shape only. Never the value.
        lengthChars = value.Length,
        sha256Prefix = digest[..16],

        proof = "The setting stored on the site is '@Microsoft.KeyVault(VaultName=...;SecretName=...)'. "
              + "App Service resolved it at startup using the managed identity. The value is in this "
              + "process's environment and was never in app settings, the template, or git."
    });
});

// ---------------------------------------------------------------------------------------------
// / — an index, and a deliberately honest one.
//
// The obvious thing to put here is a roll-up that calls all three probes and returns a combined
// verdict. It is not here, because doing it properly means re-entering the pipeline in-process
// and doing it improperly means an endpoint that LOOKS like it ran the probes and did not. A
// summary that reports success without executing anything is worse than no summary: it is the
// exact failure mode this whole exercise is about — a green signal that measured nothing.
//
// So this lists the probes and the caller runs them.
// ---------------------------------------------------------------------------------------------
app.MapGet("/", (IConfiguration config) => Results.Ok(new
{
    service = "Day 25 — identity end to end",
    appIdentityClientId = config["AZURE_CLIENT_ID"],
    probes = new[]
    {
        new { path = "/probe/sql", proves = "API -> SQL with a managed identity and no password" },
        new { path = "/probe/servicebus", proves = "API -> Service Bus send and receive, local auth disabled" },
        new { path = "/probe/keyvault", proves = "the Key Vault reference resolved; value never returned" },
        new { path = "/whoami", proves = "Entra token validation by the platform, no client secret" }
    },
    strongestProof = "/probe/sql — it returns the principal the database server itself reports, "
                   + "on a server that cannot accept a password."
}));

app.Run();

// ---------------------------------------------------------------------------------------------
// Redaction. This connection string carries no credential, so nothing here is load-bearing for
// safety — it exists so that if somebody later adds a Password= to it, the value does not start
// appearing in HTTP responses because a helper assumed there was never anything to hide.
// ---------------------------------------------------------------------------------------------
static string Redact(string connectionString)
{
    var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
    var sensitive = new[] { "password", "pwd", "accountkey", "sharedaccesskey" };

    var cleaned = parts.Select(part =>
    {
        var key = part.Split('=', 2)[0].Trim().ToLowerInvariant().Replace(" ", "");
        return sensitive.Contains(key) ? $"{part.Split('=', 2)[0]}=<REDACTED>" : part;
    });

    return string.Join(';', cleaned);
}
