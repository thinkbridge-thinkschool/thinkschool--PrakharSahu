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

        // The service-provider overload, so the interceptor can resolve the singleton counter
        // regardless of the order these extension methods are called in.
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
            options
                .UseSqlite(ResolveConnectionString(config, environment))
                .AddInterceptors(new DbQueryCounterInterceptor(
                    serviceProvider.GetRequiredService<DbQueryCounter>())));

        return services;
    }

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
