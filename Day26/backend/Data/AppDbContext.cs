using Microsoft.EntityFrameworkCore;
using QuotesApi.Models;

namespace QuotesApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>
    /// Day 20: events waiting to be published, written in the same transaction as the change
    /// that produced them. See <see cref="OutboxMessage"/> for why this table exists at all.
    /// </summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

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

        ConfigureOutbox(modelBuilder);
    }

    /// <summary>
    /// Shapes the outbox table around the one query the relay actually runs.
    /// </summary>
    /// <remarks>
    /// Carries the same EnsureCreated() caveat as the indexes above: this table only appears in
    /// databases created after it was added. The long-lived dev quotes.db will not gain it, so
    /// Day 20 uses a fresh database file.
    /// </remarks>
    private static void ConfigureOutbox(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(message => message.Id);

            // Never database-generated. The id is created in application code when the row is
            // written, because it doubles as the published MessageId and the consumer's
            // idempotency key — a value the relay has to be able to republish unchanged after
            // a crash.
            entity.Property(message => message.Id).ValueGeneratedNever();

            entity.Property(message => message.Type).IsRequired().HasMaxLength(200);
            entity.Property(message => message.AggregateType).IsRequired().HasMaxLength(100);
            entity.Property(message => message.AggregateId).IsRequired().HasMaxLength(100);
            entity.Property(message => message.Payload).IsRequired();
            entity.Property(message => message.LastError).HasMaxLength(1000);
            entity.Property(message => message.LockedBy).HasMaxLength(100);

            // Day 26. Both nullable, and both capped at a length the W3C spec makes predictable.
            //
            // A traceparent is exactly 55 characters — version(2) + trace-id(32) + parent-id(16)
            // + flags(2) + three hyphens. 64 leaves room without inviting anything larger.
            //
            // tracestate is capped at 512 because the spec permits up to 32 comma-separated
            // vendor entries, and an unbounded column here is an unbounded column written on
            // every single domain event. This is telemetry metadata: it must never be the reason
            // a business write fails, so it is bounded on the way in.
            entity.Property(message => message.TraceParent).HasMaxLength(64);
            entity.Property(message => message.TraceState).HasMaxLength(512);

            // The relay's only hot query is "give me the oldest unpublished rows". A filtered
            // index on exactly that keeps it proportional to the BACKLOG rather than to the
            // table — which matters because the table grows forever while the backlog should
            // hover near zero. Without the filter, the scan gets slower every day the system
            // works correctly.
            entity.HasIndex(message => new { message.ProcessedAt, message.NextAttemptAt })
                  .HasDatabaseName("IX_Outbox_Pending")
                  .HasFilter("\"ProcessedAt\" IS NULL");

            // Answers "did we ever emit an event for this quote?" during an incident, which is
            // the question you have at 3am and cannot answer from the broker.
            entity.HasIndex(message => new { message.AggregateType, message.AggregateId })
                  .HasDatabaseName("IX_Outbox_Aggregate");
        });
    }
}
