using EfCorePerformance.Data;
using EfCorePerformance.Models;
using Microsoft.EntityFrameworkCore;

namespace EfCorePerformance.Repositories;

// Every read here makes its tracking behaviour explicit, so the caller can compare the
// three modes side by side instead of relying on whatever the default happens to be.
public class RecordRepository
{
    private readonly AppDbContext _context;

    public RecordRepository(AppDbContext context)
    {
        _context = context;
    }

    // Single-row reads, used to test whether two separate queries hand back the same object.
    public BenchmarkRecord GetTracked(int id) => _context.Records.First(r => r.Id == id);

    public BenchmarkRecord GetUntracked(int id) => _context.Records.AsNoTracking().First(r => r.Id == id);

    public BenchmarkRecord GetUntrackedWithIdentityResolution(int id) =>
        _context.Records.AsNoTrackingWithIdentityResolution().First(r => r.Id == id);

    // Full-table reads, used for the benchmark.
    public List<BenchmarkRecord> GetAllTracked() => _context.Records.ToList();

    public List<BenchmarkRecord> GetAllUntracked() => _context.Records.AsNoTracking().ToList();

    // No change tracking, but EF still reuses one instance per primary key while it builds
    // this query's results. Useful when a join returns the same row many times.
    public List<BenchmarkRecord> GetAllUntrackedWithIdentityResolution() =>
        _context.Records.AsNoTrackingWithIdentityResolution().ToList();

    // Cross-joins every row against the single row with Id 1, so that one row shows up in the
    // result set once per table row. Counting distinct object references then shows whether EF
    // built one instance for it or thousands.
    public (int TotalRows, int DistinctAnchorInstances) RunSelfJoinIdentityDemo(bool withIdentityResolution)
    {
        var query = _context.Records
            .SelectMany(
                row => _context.Records.Where(anchor => anchor.Id == 1),
                (row, anchor) => new { Row = row, Anchor = anchor });

        var rows = withIdentityResolution
            ? query.AsNoTrackingWithIdentityResolution().ToList()
            : query.AsNoTracking().ToList();

        var distinctAnchors = rows
            .Select(x => x.Anchor)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Count();

        return (rows.Count, distinctAnchors);
    }
}
