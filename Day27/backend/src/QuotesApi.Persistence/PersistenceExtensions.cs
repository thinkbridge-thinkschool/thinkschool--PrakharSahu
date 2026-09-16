using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace QuotesApi.Persistence;

/// <summary>
/// Registers the database. Split out of the old monolithic <c>AddInfrastructure</c>, which
/// registered persistence, the quote repository, authentication and authorization policies in
/// one method — four different modules' concerns in one place, which is precisely what the
/// module split exists to stop.
/// </summary>
/// <remarks>
/// <para>
/// <b>One shared <see cref="AppDbContext"/> is a deliberate, recorded compromise.</b> A strict
/// modular monolith gives each module its own context so no module can see another's tables.
/// This solution keeps one, which means every module's Infrastructure transitively sees every
/// module's entities.
/// </para>
/// <para>
/// It is confined to this single project so the exception is bounded and visible:
/// <c>QuotesApi.ArchitectureTests</c> permits exactly the edge
/// <c>*.Infrastructure -&gt; QuotesApi.Persistence</c> and fails the build on any other
/// cross-module reference. The compromise is one line in one test rather than an invisible
/// property of the whole codebase.
/// </para>
/// <para>
/// The reason to keep it is the Day 20 outbox guarantee: a quote and its outbox row must commit
/// in the same transaction. Two contexts would have to share a <see cref="SqliteConnection"/>
/// and enlist in one another's transaction, which is achievable and is a change worth making
/// deliberately rather than as a side effect of a restructure.
/// </para>
/// </remarks>
public static class PersistenceExtensions
{
    private const string DefaultDatabaseFile = "quotes.db";

    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration config,
        IHostEnvironment environment)
    {
        // Day 21: counts every SQL command EF sends, which is the "DB queries/sec" the exercise
        // asks to be measured. It belongs to the DbContext, and must exist whether or not
        // caching is wired up — otherwise the "before" arm has nothing to count.
        services.AddSingleton<DbQueryCounter>();

        // -----------------------------------------------------------------------------------
        // Which provider, decided by the SHAPE of the connection string.
        //
        // Local development stays on SQLite: no Azure account, no network, no cost, and
        // verify-hardening.sh keeps working on a laptop. The deployed app runs on Azure SQL,
        // and the switch is configuration rather than a build flag so the same image runs in
        // both places.
        //
        // The discriminator is the connection string's own grammar -- a SQLite one names a
        // FILE, a SQL Server one names a HOST. That is a more honest test than checking the
        // environment name, which would pick the wrong provider the moment somebody runs
        // Production locally to reproduce a bug.
        //
        // Authentication is deliberately NOT part of this decision. The deployed connection
        // string carries `Authentication=Active Directory Managed Identity`, so the token comes
        // from the platform and there is no password to configure, rotate or leak -- which is
        // the Day 25 posture, now applied to this API too.
        // -----------------------------------------------------------------------------------
        var configured = config.GetConnectionString("DefaultConnection");

        // The service-provider overload, so the interceptor can resolve the singleton counter
        // regardless of the order these extension methods are called in.
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            if (IsSqlServer(configured))
            {
                options.UseSqlServer(configured, sql =>
                    // Azure SQL serverless auto-pauses when idle, and the connection that wakes
                    // it can take tens of seconds. Without a retry the first request after a
                    // quiet night fails with a timeout that reads like an outage and is a cold
                    // start.
                    sql.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(20),
                        errorNumbersToAdd: null));
            }
            else
            {
                options.UseSqlite(ResolveConnectionString(config, environment));
            }

            options.AddInterceptors(new DbQueryCounterInterceptor(
                serviceProvider.GetRequiredService<DbQueryCounter>()));
        });

        return services;
    }

    /// <summary>
    /// True when the connection string points at a SQL Server host rather than a SQLite file.
    /// </summary>
    /// <remarks>
    /// Deliberately a cheap structural test rather than a parse.
    /// <see cref="Microsoft.Data.Sqlite.SqliteConnectionStringBuilder"/> accepts an Azure SQL
    /// connection string without complaint and treats the whole thing as a file name -- producing
    /// a database file literally called
    /// <c>Server=tcp:sql-....database.windows.net,1433;...</c> and an application that starts
    /// perfectly while writing to nothing anybody will ever read. Checking first is what stops
    /// that failure, which is silent in exactly the way this codebase keeps learning to distrust.
    /// </remarks>
    private static bool IsSqlServer(string? connectionString) =>
        !string.IsNullOrWhiteSpace(connectionString)
        && (connectionString.Contains("Server=tcp:", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase));

    private static string ResolveConnectionString(IConfiguration config, IHostEnvironment environment)
    {
        var configured = config.GetConnectionString("DefaultConnection");
        var builder = new SqliteConnectionStringBuilder(
            string.IsNullOrWhiteSpace(configured)
                ? $"Data Source={DefaultDatabaseFile}"
                : configured);

        // ":memory:" and shared-cache in-memory names are not file paths; integration tests use
        // them and must be left exactly as written.
        if (!string.IsNullOrWhiteSpace(builder.DataSource)
            && !builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(
                Path.Combine(environment.ContentRootPath, builder.DataSource));
        }

        return builder.ToString();
    }
}
