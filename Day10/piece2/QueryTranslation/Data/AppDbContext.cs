using Microsoft.EntityFrameworkCore;
using QueryTranslation.Models;

namespace QueryTranslation.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(entity =>
        {
            entity.ToTable("Day10P2_Categories");
            entity.Property(c => c.Name).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Day10P2_Products");
            entity.Property(p => p.Sku).HasMaxLength(32).IsRequired();
            entity.Property(p => p.Name).HasMaxLength(200).IsRequired();
            entity.Property(p => p.Price).HasPrecision(18, 2);

            // Left as nvarchar(max) on purpose. These are the columns that make a
            // SELECT * expensive, and the ones a projection gets to skip.
            entity.Property(p => p.Description).IsRequired();
            entity.Property(p => p.InternalNotes).IsRequired();

            entity.HasIndex(p => p.CategoryId);

            entity.HasOne(p => p.Category)
                .WithMany(c => c.Products)
                .HasForeignKey(p => p.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
