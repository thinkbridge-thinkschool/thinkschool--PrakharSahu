using System.Diagnostics;
using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QueryTranslation.Data;
using QueryTranslation.Dtos;
using QueryTranslation.Infrastructure;
using QueryTranslation.Repositories;

namespace QueryTranslation;

public class Program
{
    private static readonly SqlCapture Capture = new();

    public static void Main()
    {
        // The connection string lives in .env (gitignored), never in appsettings.json.
        Env.Load();

        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
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

        var productCount = config.GetValue("Benchmark:ProductCount", 10_000);
        var categoryCount = config.GetValue("Benchmark:CategoryCount", 10);
        var iterations = config.GetValue("Benchmark:Iterations", 9);

        var options = BuildOptions(connectionString, sensitiveData: true);

        Seed(options, productCount, categoryCount);
        ShowLoggingSetup(connectionString);
        ShowGeneratedSql(options);
        ShowNavigationProjection(options);
        Benchmark(options, iterations);
        ShowClientSideEvaluation(options);
        ShowCoveringIndexEffect(options);

        Console.WriteLine("\nDone.");
    }

    // LogTo is the whole logging story for a console app: no DI, no logging provider, just a
    // callback. Filtering to the Database.Command category keeps out the model-building noise.
    // EnableSensitiveDataLogging is what turns '@__minPrice_0=?' into the actual value, which is
    // why it is a development-only switch: parameter values are exactly the thing you do not
    // want appearing in a shared log.
    private static DbContextOptions<AppDbContext> BuildOptions(string connectionString, bool sensitiveData)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .LogTo(
                Capture.Log,
                new[] { DbLoggerCategory.Database.Command.Name },
                LogLevel.Information);

        if (sensitiveData)
        {
            builder.EnableSensitiveDataLogging();
        }

        return builder.Options;
    }

    private static void Seed(DbContextOptions<AppDbContext> options, int productCount, int categoryCount)
    {
        Console.WriteLine("[+] Preparing SQL Server schema and seed data...");
        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();

        if (context.Products.Count() == productCount)
        {
            Console.WriteLine($"    {productCount:N0} products already present.");
            return;
        }

        context.Database.ExecuteSqlRaw("DELETE FROM Day10P2_Products; DELETE FROM Day10P2_Categories;");

        for (var i = 1; i <= categoryCount; i++)
        {
            context.Categories.Add(new Models.Category { Name = $"Category {i}" });
        }
        context.SaveChanges();

        // Seeded in T-SQL rather than through the change tracker. 10,000 rows carrying roughly a
        // kilobyte of text each is slow to push through SaveChanges, and the seed is not the thing
        // being measured here.
        // ExecuteSql rather than ExecuteSqlRaw so the two counts arrive as parameters. They come
        // from configuration rather than user input, but TOP (@p0) is valid T-SQL and there is no
        // reason to hand-build the statement.
        context.Database.ExecuteSql($"""
            INSERT INTO Day10P2_Products
                (Sku, Name, Price, StockQuantity, Weight, IsDiscontinued, CreatedUtc,
                 Description, InternalNotes, CategoryId)
            SELECT TOP ({productCount})
                CONCAT('SKU-', RIGHT('00000' + CAST(n.rn AS varchar(6)), 6)),
                CONCAT('Product ', n.rn),
                CAST(((n.rn % 500) + 1) * 1.99 AS decimal(18,2)),
                n.rn % 250,
                ((n.rn % 40) + 1) * 0.25,
                CASE WHEN n.rn % 17 = 0 THEN 1 ELSE 0 END,
                DATEADD(minute, -n.rn, SYSUTCDATETIME()),
                CAST(REPLICATE(CAST('Long marketing description text. ' AS nvarchar(max)), 20) AS nvarchar(max)),
                CAST(REPLICATE(CAST('Internal warehouse note. ' AS nvarchar(max)), 12) AS nvarchar(max)),
                ((n.rn - 1) % {categoryCount}) + 1
            FROM (
                SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
                FROM sys.all_objects a CROSS JOIN sys.all_objects b
            ) AS n
            """);

        Console.WriteLine($"    Seeded {context.Products.Count():N0} products across {categoryCount} categories.");
    }

    // Shows the one switch that changes what ends up in the log, using the same query twice.
    private static void ShowLoggingSetup(string connectionString)
    {
        Console.WriteLine("\n--- 1. Logging the generated SQL ---");

        // A local variable, not a const. EF parameterizes captured variables but inlines
        // constants straight into the SQL as literals, so a const here would produce
        // "Price > 500.0" and there would be no parameter to show.
        var minPrice = 500m;

        Capture.Clear();
        using (var quiet = new AppDbContext(BuildOptions(connectionString, sensitiveData: false)))
        {
            quiet.Products.Where(p => p.Price > minPrice).Select(p => p.Id).Take(1).ToList();
        }
        Console.WriteLine($"    Default logging          -> Parameters=[{Capture.LastParameterHeader}]");

        Capture.Clear();
        using (var verbose = new AppDbContext(BuildOptions(connectionString, sensitiveData: true)))
        {
            verbose.Products.Where(p => p.Price > minPrice).Select(p => p.Id).Take(1).ToList();
        }
        Console.WriteLine($"    EnableSensitiveDataLogging -> Parameters=[{Capture.LastParameterHeader}]");
        Console.WriteLine("    (values are visible in the second one, which is why it is dev-only)");
    }

    // The core of the exercise: same filter, same rows, two very different statements.
    private static void ShowGeneratedSql(DbContextOptions<AppDbContext> options)
    {
        Console.WriteLine("\n--- 2. Whole entity versus projection ---");
        using var context = new AppDbContext(options);

        Capture.Clear();
        context.Products.AsNoTracking().Where(p => p.CategoryId == 1).ToList();
        var entitySql = Capture.LastStatement;

        Capture.Clear();
        context.Products.AsNoTracking()
            .Where(p => p.CategoryId == 1)
            .Select(p => new ProductListItemDto
            {
                Id = p.Id,
                Name = p.Name,
                Price = p.Price,
                StockQuantity = p.StockQuantity,
            })
            .ToList();
        var dtoSql = Capture.LastStatement;

        Console.WriteLine($"\n    Whole entity ({SqlCapture.CountSelectedColumns(entitySql)} columns):");
        Console.WriteLine($"      {SqlCapture.Shorten(entitySql)}");
        Console.WriteLine($"\n    Projected DTO ({SqlCapture.CountSelectedColumns(dtoSql)} columns):");
        Console.WriteLine($"      {SqlCapture.Shorten(dtoSql)}");
        Console.WriteLine("\n    Description and InternalNotes are absent from the second statement,");
        Console.WriteLine("    so the nvarchar(max) payload never crosses the wire.");
    }

    // A projection that reaches through a navigation compiles into a JOIN and still only selects
    // the columns named. Include is the alternative, and it drags both whole entities back.
    private static void ShowNavigationProjection(DbContextOptions<AppDbContext> options)
    {
        Console.WriteLine("\n--- 3. Projecting across a navigation ---");
        using var context = new AppDbContext(options);

        Capture.Clear();
        context.Products.AsNoTracking()
            .Where(p => p.CategoryId == 1)
            .Include(p => p.Category)
            .ToList();
        var includeSql = Capture.LastStatement;

        Capture.Clear();
        context.Products.AsNoTracking()
            .Where(p => p.CategoryId == 1)
            .Select(p => new ProductWithCategoryDto
            {
                Name = p.Name,
                Price = p.Price,
                CategoryName = p.Category!.Name,
            })
            .ToList();
        var joinSql = Capture.LastStatement;

        Console.WriteLine($"    Include(p => p.Category)   -> {SqlCapture.CountSelectedColumns(includeSql)} columns selected");
        Console.WriteLine($"    Select(... p.Category.Name) -> {SqlCapture.CountSelectedColumns(joinSql)} columns selected");
        Console.WriteLine($"      {SqlCapture.Shorten(joinSql, 220)}");
    }

    private static void Benchmark(DbContextOptions<AppDbContext> options, int iterations)
    {
        Console.WriteLine($"\n--- 4. Cost of the two shapes, median of {iterations} iterations ---");
        Console.WriteLine($"    {"Query",-34} {"Time (ms)",10} {"Alloc (MB)",12} {"Rows",8}");

        Measure("Whole entity (11 columns)", options, iterations, context =>
            context.Products.AsNoTracking().Where(p => p.CategoryId == 1).ToList().Count);

        Measure("Projected DTO (4 columns)", options, iterations, context =>
            context.Products.AsNoTracking()
                .Where(p => p.CategoryId == 1)
                .Select(p => new ProductListItemDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Price = p.Price,
                    StockQuantity = p.StockQuantity,
                })
                .ToList().Count);
    }

    private static void Measure(
        string label,
        DbContextOptions<AppDbContext> options,
        int iterations,
        Func<AppDbContext, int> run)
    {
        using (var warmup = new AppDbContext(options))
        {
            run(warmup);
        }

        var times = new List<double>(iterations);
        var allocations = new List<double>(iterations);
        var rows = 0;

        for (var i = 0; i < iterations; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var startAlloc = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();

            using (var context = new AppDbContext(options))
            {
                rows = run(context);
            }

            stopwatch.Stop();
            times.Add(stopwatch.Elapsed.TotalMilliseconds);
            allocations.Add((GC.GetTotalAllocatedBytes(precise: true) - startAlloc) / 1024.0 / 1024.0);
        }

        Console.WriteLine($"    {label,-34} {Median(times),10:F1} {Median(allocations),12:F2} {rows,8:N0}");
    }

    // Two failure modes with the same name and completely different ergonomics.
    private static void ShowClientSideEvaluation(DbContextOptions<AppDbContext> options)
    {
        Console.WriteLine("\n--- 5. Accidental client-side evaluation ---");
        using var context = new AppDbContext(options);
        var repo = new ProductRepository(context);

        // (a) The silent one. GetAllAsEnumerable returns IEnumerable<Product>, so this Where is
        // Enumerable.Where and runs in this process after every row has been fetched.
        Capture.Clear();
        var clientSide = repo.GetAllAsEnumerable().Where(p => p.Price > 900m).ToList();
        var clientSql = Capture.LastStatement;
        var clientHasWhere = clientSql.Contains("WHERE", StringComparison.OrdinalIgnoreCase);

        Capture.Clear();
        var serverSide = repo.GetAllAsQueryable().Where(p => p.Price > 900m).ToList();
        var serverSql = Capture.LastStatement;
        var serverHasWhere = serverSql.Contains("WHERE", StringComparison.OrdinalIgnoreCase);

        var tableRows = context.Products.Count();

        Console.WriteLine($"    Table holds {tableRows:N0} rows.");
        Console.WriteLine($"    IEnumerable + Where -> kept {clientSide.Count,6:N0}, fetched {tableRows,6:N0}, SQL has WHERE: {clientHasWhere}");
        Console.WriteLine($"    IQueryable  + Where -> kept {serverSide.Count,6:N0}, fetched {serverSide.Count,6:N0}, SQL has WHERE: {serverHasWhere}");
        Console.WriteLine("    Same answer both times. The first pulled every row (all 11 columns,");
        Console.WriteLine("    nvarchar(max) included) and discarded most of them in memory, and");
        Console.WriteLine("    nothing in the API or the compiler warned about it.");
        Console.WriteLine($"      {SqlCapture.Shorten(clientSql, 150)}");

        // (b) The loud one. Since EF Core 3.0 an untranslatable predicate is an exception rather
        // than a silent fallback, which is the better of the two behaviours.
        try
        {
            context.Products.AsNoTracking()
                .Where(p => Normalize(p.Sku) == "SKU-000001")
                .ToList();
            Console.WriteLine("    Untranslatable predicate did NOT throw (unexpected).");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"\n    Untranslatable predicate threw, as it should:");
            Console.WriteLine($"      {SqlCapture.Shorten(ex.Message, 260)}");
        }

        // The translatable spelling of the same intent. ToUpper maps onto UPPER().
        Capture.Clear();
        var translated = context.Products.AsNoTracking()
            .Where(p => p.Sku.ToUpper() == "SKU-000001")
            .Select(p => p.Sku)
            .ToList();
        Console.WriteLine($"    Rewritten with ToUpper() -> translated fine, {translated.Count} row(s), SQL contains UPPER: " +
                          $"{Capture.LastStatement.Contains("UPPER", StringComparison.OrdinalIgnoreCase)}");
    }

    // A C# method EF has no way to turn into SQL.
    private static string Normalize(string sku) => sku.Trim().ToUpperInvariant();

    // Worth being precise about what a projection does and does not save. Fewer columns in the
    // SELECT list cuts the payload and the materialization work, but the storage engine still
    // reads whatever pages the plan touches. Only a covering index changes the IO.
    private static void ShowCoveringIndexEffect(DbContextOptions<AppDbContext> options)
    {
        Console.WriteLine("\n--- 6. Does the projection reduce IO as well as payload? ---");
        using var context = new AppDbContext(options);

        context.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_Day10P2_Products_Covering ON Day10P2_Products;");

        var before = MeasureLogicalReads(context, projected: true);
        Console.WriteLine($"    Projection, no covering index -> {before:N0} logical reads");

        context.Database.ExecuteSqlRaw("""
            CREATE NONCLUSTERED INDEX IX_Day10P2_Products_Covering
            ON Day10P2_Products (CategoryId)
            INCLUDE (Name, Price, StockQuantity);
            """);

        var after = MeasureLogicalReads(context, projected: true);
        var entity = MeasureLogicalReads(context, projected: false);

        Console.WriteLine($"    Projection, covering index    -> {after:N0} logical reads");
        Console.WriteLine($"    Whole entity, covering index  -> {entity:N0} logical reads");
        Console.WriteLine("    The projection can use the narrow index; the whole-entity query still");
        Console.WriteLine("    needs the wide columns, so it goes back to the clustered index.");
    }

    // Reads the IO actually performed, straight from SQL Server rather than inferred.
    private static long MeasureLogicalReads(AppDbContext context, bool projected)
    {
        context.Database.ExecuteSqlRaw("ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;");

        if (projected)
        {
            context.Products.AsNoTracking()
                .Where(p => p.CategoryId == 1)
                .Select(p => new ProductListItemDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Price = p.Price,
                    StockQuantity = p.StockQuantity,
                })
                .ToList();
        }
        else
        {
            context.Products.AsNoTracking().Where(p => p.CategoryId == 1).ToList();
        }

        return context.Database
            .SqlQueryRaw<long>("""
                SELECT TOP 1 CAST(qs.total_logical_reads AS bigint) AS Value
                FROM sys.dm_exec_query_stats qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
                WHERE st.text LIKE '%Day10P2_Products%'
                  AND st.text NOT LIKE '%dm_exec_query_stats%'
                ORDER BY qs.last_execution_time DESC
                """)
            .AsEnumerable()
            .FirstOrDefault();
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
