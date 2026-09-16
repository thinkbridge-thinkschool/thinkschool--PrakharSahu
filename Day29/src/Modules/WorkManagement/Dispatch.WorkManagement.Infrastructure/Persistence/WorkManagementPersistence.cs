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
        services.AddDbContext<WorkManagementDbContext>(options =>
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
