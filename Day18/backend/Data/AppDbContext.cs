using Microsoft.EntityFrameworkCore;
using QuotesApi.Models;

namespace QuotesApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Two accounts with the same email means login picks whichever row comes back first,
        // so one of the two passwords silently stops working. The registration endpoint also
        // checks for duplicates before inserting — belt and braces, but deliberately:
        //
        // This index only reaches databases created AFTER it was added, because startup uses
        // EnsureCreated() rather than Migrate(), and EnsureCreated does nothing when the
        // tables already exist. The long-lived dev quotes.db predates this and already holds
        // duplicate emails, so it will never gain the constraint. The application-level check
        // is what protects that database; this index is what protects every new one.
        modelBuilder.Entity<User>()
            .HasIndex(user => user.Email)
            .IsUnique();

        // Same author + same words = a double post. The POST and PUT endpoints check for this
        // before writing; this index is what closes the gap between that check and the insert,
        // where two simultaneous requests could both look, both see nothing, and both save.
        //
        // Deliberately weaker than the endpoint check in two ways. It is filtered on IsDeleted,
        // so deleting a quote frees the author to post it again. And it compares exactly, while
        // the endpoint folds case and trims — so "Be kind" and "be kind" are caught by the
        // endpoint but not by the index.
        //
        // Carries the same EnsureCreated() caveat as the index above: it only reaches databases
        // created after this line was added. The existing dev quotes.db will never gain it.
        modelBuilder.Entity<Quote>()
            .HasIndex(quote => new { quote.Author, quote.Text })
            .IsUnique()
            .HasFilter("\"IsDeleted\" = 0");
    }
}
