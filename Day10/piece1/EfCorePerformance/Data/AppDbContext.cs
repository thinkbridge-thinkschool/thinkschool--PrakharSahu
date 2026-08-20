using EfCorePerformance.Models;
using Microsoft.EntityFrameworkCore;

namespace EfCorePerformance.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<BenchmarkRecord> Records { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BenchmarkRecord>().ToTable("Day10_BenchmarkRecords");
    }
}