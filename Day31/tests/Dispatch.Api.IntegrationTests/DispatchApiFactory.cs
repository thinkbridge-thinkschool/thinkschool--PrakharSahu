using Dispatch.Billing.Infrastructure.Persistence;
using Dispatch.SharedKernel;
using Dispatch.Scheduling.Infrastructure.Persistence;
using Dispatch.WorkManagement.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dispatch.Api.IntegrationTests;

/// <summary>
/// Boots the real application in process, against a real database that belongs to this test run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a real database rather than an in-memory provider.</b> Everything interesting about this
/// application happens at the boundary EF Core translates: owned collections, value converters, a
/// filtered unique index, a serializable transaction. The in-memory provider implements none of
/// that, so a suite built on it would pass while the deployed system failed — the exact shape of
/// the bug Day 29 hit, where a dictionary made a missing <c>SaveChanges</c> invisible.
/// </para>
/// <para>
/// <b>Why a fresh database per run.</b> The name carries a GUID, so two runs on one machine — or a
/// developer and CI sharing a server — cannot see each other's rows. It is created, migrated, and
/// dropped. Sharing one database and deleting rows between tests sounds cheaper and fails the
/// first time a test leaves a row behind that the next one counts.
/// </para>
/// <para>
/// <b>What this still does not test.</b> There is no socket: <c>WebApplicationFactory</c> uses an
/// in-memory transport, so TLS, Kestrel's own limits, proxies and real network failures are all
/// absent. That is what the end-to-end test is for, and why there is one.
/// </para>
/// </remarks>
public sealed class DispatchApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>The clock every request sees. Advance it to move the application through time.</summary>
    public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);

    private readonly string _databaseName = $"dispatch_it_{Guid.NewGuid():N}";

    /// <summary>
    /// Where the test SQL Server lives. Defaults to the local container this repository documents.
    /// </summary>
    /// <remarks>
    /// Read from the environment so CI can point at its own service container without editing
    /// code. The default is the local Docker container, so a developer who followed the README
    /// needs no configuration at all.
    /// </remarks>
    private static string MasterConnectionString =>
        Environment.GetEnvironmentVariable("DISPATCH_TEST_SQL")
        ?? "Server=localhost,14333;Database=master;User Id=sa;"
           + "Password=Dispatch-Test-Local;Encrypt=True;TrustServerCertificate=True;Connection Timeout=60";

    public static bool Configured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPATCH_TEST_SQL"));

    public const string SkipReason =
        "Set DISPATCH_TEST_SQL to a SQL Server connection string to run the integration tests. "
        + "See README: docker run ... mcr.microsoft.com/mssql/server:2022-latest";

    private string ConnectionString =>
        new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = _databaseName }
            .ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Configuration, not a service override. Program.cs reads ConnectionStrings:Dispatch and
        // refuses to start without it; setting it here exercises that real path rather than
        // reaching past it to swap a DbContext, which would leave the composition untested.
        builder.UseSetting("ConnectionStrings:Dispatch", ConnectionString);

        builder.ConfigureServices(services =>
        {
            // The two sweepers poll every minute and would otherwise run during the suite,
            // mutating rows underneath assertions. Their behaviour is covered by unit tests
            // against the aggregate; what is under test here is the HTTP pipeline.
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();

            // ---------------------------------------------------------------------------
            // A controllable clock.
            //
            // Not a convenience. Two domain rules are written against wall-clock time -- a
            // window may not start in the past, and work may not start before its window
            // opens -- so an order cannot be driven to Completed without either controlling
            // time or making the test sleep for hours.
            //
            // The application already depends on IClock rather than DateTimeOffset.UtcNow
            // precisely so this is possible. Overriding it here uses that seam instead of
            // weakening the rule to suit the test, which would have been the other way to
            // make this pass.
            // ---------------------------------------------------------------------------
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        await using (var connection = new SqlConnection(MasterConnectionString))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_databaseName}]";
            await create.ExecuteNonQueryAsync();
        }

        // Migrate rather than EnsureCreated. EnsureCreated builds the schema from the model and
        // silently skips migrations, so a migration that is broken or missing would never be
        // noticed here — and the deployed database is built by migrations.
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkManagementDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<SchedulingDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (!Configured)
        {
            return;
        }

        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();

        // SINGLE_USER WITH ROLLBACK IMMEDIATE, because the connection pool may still hold idle
        // connections to this database and DROP would otherwise fail with "currently in use" —
        // leaving an orphaned database behind on every run.
        drop.CommandText =
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
            + $"DROP DATABASE [{_databaseName}];";
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>One database per test class, shared by the tests inside it.</summary>
/// <remarks>
/// A class fixture rather than a collection fixture: creating a database costs about a second, and
/// class-level isolation means a test that leaves data behind cannot reach into another class.
/// </remarks>
/// <summary>A clock the test controls, in place of <c>SystemClock</c>.</summary>
public sealed class TestClock(DateTimeOffset now) : IClock
{
    private DateTimeOffset _now = now;

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan by) => _now += by;
}

[CollectionDefinition(nameof(DispatchApiCollection))]
public sealed class DispatchApiCollection : ICollectionFixture<DispatchApiFactory>;
