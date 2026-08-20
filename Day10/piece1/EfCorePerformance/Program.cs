using System.Diagnostics;
using DotNetEnv;
using EfCorePerformance.Data;
using EfCorePerformance.Models;
using EfCorePerformance.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EfCorePerformance;

public class Program
{
    public static void Main()
    {
        // The connection string lives in .env (gitignored), never in appsettings.json.
        // Env.Load() no-ops when there is no .env file, in which case the real
        // environment variables are expected to already be set.
        Env.Load();

        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                "No connection string found. Copy .env.example to .env and fill in the real value:\n" +
                "  ConnectionStrings__DefaultConnection=\"Server=...;Database=...;User Id=...;Password=...\"");
            Environment.ExitCode = 1;
            return;
        }

        var rowCount = config.GetValue("Benchmark:RowCount", 10_000);
        var iterations = config.GetValue("Benchmark:Iterations", 5);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        Seed(options, rowCount);
        ShowIdentityMap(options);
        ShowIdentityResolutionScope(options, rowCount);
        RunBenchmark(options, rowCount, iterations);
    }

    private static void Seed(DbContextOptions<AppDbContext> options, int rowCount)
    {
        Console.WriteLine("[+] Preparing SQL Server schema and seed data...");
        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();

        var existing = context.Records.Count();
        if (existing == rowCount)
        {
            Console.WriteLine($"    {existing:N0} rows already present.");
            return;
        }

        // Any other count means a previous run seeded a different size, so start clean
        // rather than benchmarking against a row count we did not intend.
        if (existing > 0)
        {
            context.Records.ExecuteDelete();
        }

        var seed = Enumerable.Range(1, rowCount)
            .Select(i => new BenchmarkRecord { Name = $"Record #{i}", Value = i * 2.5 });
        context.Records.AddRange(seed);
        context.SaveChanges();
        Console.WriteLine($"    Seeded {rowCount:N0} rows.");
    }

    // The change tracker keeps an identity map keyed by entity type + primary key. A second
    // tracked query for a key it already holds returns the instance it is already tracking
    // instead of building a new one. AsNoTracking has no map, so it always builds a new one.
    private static void ShowIdentityMap(DbContextOptions<AppDbContext> options)
    {
        Console.WriteLine("\n--- 1. Identity map: two separate queries, one DbContext ---");
        using var context = new AppDbContext(options);
        var repo = new RecordRepository(context);

        var trackedA = repo.GetTracked(1);
        var trackedB = repo.GetTracked(1);
        Report("Tracked", ReferenceEquals(trackedA, trackedB), context.ChangeTracker.Entries().Count());

        // A separate context so the count below reflects only the untracked reads.
        using var freshContext = new AppDbContext(options);
        var freshRepo = new RecordRepository(freshContext);
        var untrackedA = freshRepo.GetUntracked(1);
        var untrackedB = freshRepo.GetUntracked(1);
        Report("Untracked", ReferenceEquals(untrackedA, untrackedB), freshContext.ChangeTracker.Entries().Count());

        // Identity resolution does not help here, because these are two separate queries.
        // Section 2 shows the case where it does.
        using var resolutionContext = new AppDbContext(options);
        var resolutionRepo = new RecordRepository(resolutionContext);
        var resolvedA = resolutionRepo.GetUntrackedWithIdentityResolution(1);
        var resolvedB = resolutionRepo.GetUntrackedWithIdentityResolution(1);
        Report("Resolution", ReferenceEquals(resolvedA, resolvedB), resolutionContext.ChangeTracker.Entries().Count());

        static void Report(string label, bool sameInstance, int trackedEntries)
        {
            var noun = trackedEntries == 1 ? "entry" : "entries";
            Console.WriteLine($"    {label,-10} -> ReferenceEquals: {sameInstance,-5}, change tracker holds {trackedEntries} {noun}");
        }
    }

    // The part that is easy to get wrong: no-tracking identity resolution is scoped to one
    // query's materialization, not to the lifetime of the context. Cross-joining every row
    // against a single anchor row puts that one row in the result set thousands of times,
    // which is the only way to actually see the difference.
    private static void ShowIdentityResolutionScope(DbContextOptions<AppDbContext> options, int rowCount)
    {
        Console.WriteLine($"\n--- 2. Identity resolution scope: one row repeated {rowCount:N0}x inside one query ---");
        using var context = new AppDbContext(options);
        var repo = new RecordRepository(context);

        Report("AsNoTracking()", repo.RunSelfJoinIdentityDemo(withIdentityResolution: false));
        Report("AsNoTrackingWithIdentityResolution()", repo.RunSelfJoinIdentityDemo(withIdentityResolution: true));

        static void Report(string label, (int TotalRows, int DistinctAnchorInstances) result)
        {
            var noun = result.DistinctAnchorInstances == 1 ? "object" : "objects";
            Console.WriteLine($"    {label,-36} -> {result.TotalRows,6:N0} rows, {result.DistinctAnchorInstances,6:N0} distinct anchor {noun}");
        }
    }

    private static void RunBenchmark(DbContextOptions<AppDbContext> options, int rowCount, int iterations)
    {
        Console.WriteLine($"\n--- 3. Benchmark: {rowCount:N0}-row read, median of {iterations} iterations ---");
        Console.WriteLine($"    {"Mode",-38} {"Time (ms)",10} {"Alloc (MB)",12}");

        Measure("Tracked (default)", options, iterations,
            ctx => new RecordRepository(ctx).GetAllTracked());

        Measure("AsNoTracking()", options, iterations,
            ctx => new RecordRepository(ctx).GetAllUntracked());

        Measure("AsNoTrackingWithIdentityResolution()", options, iterations,
            ctx => new RecordRepository(ctx).GetAllUntrackedWithIdentityResolution());
    }

    private static void Measure(
        string label,
        DbContextOptions<AppDbContext> options,
        int iterations,
        Func<AppDbContext, List<BenchmarkRecord>> read)
    {
        // Throwaway pass so JIT, the connection pool handshake and SQL Server's plan cache
        // are all paid for before anything is timed.
        using (var warmup = new AppDbContext(options))
        {
            read(warmup);
        }

        var times = new List<double>(iterations);
        var allocations = new List<double>(iterations);

        for (var i = 0; i < iterations; i++)
        {
            // Every iteration gets its own context. Reusing one would let the previous
            // iteration's tracked entities sit in the change tracker and distort both numbers.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var startAlloc = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();

            using (var context = new AppDbContext(options))
            {
                read(context);
            }

            stopwatch.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - startAlloc;

            times.Add(stopwatch.Elapsed.TotalMilliseconds);
            allocations.Add(allocated / 1024.0 / 1024.0);
        }

        Console.WriteLine($"    {label,-38} {Median(times),10:F1} {Median(allocations),12:F2}");
    }

    // Median rather than mean: a single slow round trip to the database should not
    // decide the result.
    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
