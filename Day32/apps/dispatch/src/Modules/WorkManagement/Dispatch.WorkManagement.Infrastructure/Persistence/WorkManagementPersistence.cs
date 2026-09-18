using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.WorkManagement.Infrastructure.Persistence;

/// <summary>
/// Registers this module's database context.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>AddWorkManagement</c> on purpose. A module decides which services it needs;
/// the <em>host</em> decides where the data lives, because a connection string is a deployment
/// fact rather than a design one. Keeping them apart is what lets the same module run against
/// SQL Server in Azure and SQLite in a test without the module knowing either exists.
/// </para>
/// <para>
/// <c>EnableRetryOnFailure</c> is not optional against Azure SQL serverless. The database
/// auto-pauses when idle and the connection that wakes it can take tens of seconds; without a
/// retry the first request after a quiet period fails with a timeout that reads like an outage
/// and is a cold start. That exact failure cost real time on Day 17.
/// </para>
/// </remarks>
public static class WorkManagementPersistence
{
    public static IServiceCollection AddWorkManagementPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        // ---------------------------------------------------------------------------------
        // Pooled, not plain AddDbContext.
        //
        // A DbContext is cheap to use and not cheap to CREATE: each one builds its own internal
        // service provider, resolves its model, and wires up change tracking. Under load that
        // construction cost is paid on every single request and shows up as latency that no
        // query tuning can remove, because it happens before any SQL is sent.
        //
        // Pooling resets and reuses instances instead. The constraint it imposes is the reason
        // it is safe here: a pooled context may only take DbContextOptions, so it cannot capture
        // per-request state that would leak between requests.
        // ---------------------------------------------------------------------------------
        services.AddDbContextPool<WorkManagementDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(
                    maxRetryCount: 6,
                    maxRetryDelay: TimeSpan.FromSeconds(20),
                    errorNumbersToAdd: null);

                // Each module owns its own migrations history table, in its own schema. Sharing
                // one table would make three independently-migratable modules share a single
                // append-only log, so `dotnet ef migrations add` in one module would see the
                // others' migrations as pending and try to apply them.
                sql.MigrationsHistoryTable("__EFMigrationsHistory", WorkManagementDbContext.Schema);
            }));

        return services;
    }
}
