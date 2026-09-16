using Dispatch.Billing.Application.Invoices;
using Dispatch.Billing.Domain.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Billing.Infrastructure.Persistence;

/// <summary>
/// The Billing module's own database context, over the <c>billing</c> schema.
/// </summary>
public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public const string Schema = "billing";

    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BillingDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// How an <see cref="Invoice"/> is stored.
/// </summary>
public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices");

        builder.HasKey(invoice => invoice.Id);

        builder.Property(invoice => invoice.Id)
            .HasConversion(id => id.Value, value => new InvoiceId(value))
            .ValueGeneratedNever();

        // Both plain Guids. Billing references a work order and a customer and owns neither, and
        // it deliberately does not import their id types from other contexts.
        builder.Property(invoice => invoice.WorkOrderId).IsRequired();
        builder.Property(invoice => invoice.CustomerId).IsRequired();

        // ------------------------------------------------------------------------------------
        // Money, owned, with an explicit precision.
        //
        // decimal(19,4) rather than EF's default decimal(18,2). Two decimal places is enough to
        // STORE a currency and not enough to compute one: a rate of 0.875 per minute over 47
        // minutes rounds differently depending on when you round, and money that disagrees with
        // itself by a penny is an audit finding. Four places keeps the intermediate value and
        // rounds once, at the point of presentation.
        //
        // The currency travels with the amount because a bare decimal is not money. Invoice.Money
        // refuses to add two different currencies rather than silently producing a number, and
        // storing the code alongside is what lets that check survive a round trip.
        // ------------------------------------------------------------------------------------
        builder.OwnsOne(invoice => invoice.Total, money =>
        {
            money.Property(m => m.Amount).HasColumnName("TotalAmount")
                .HasPrecision(19, 4).IsRequired();
            money.Property(m => m.Currency).HasColumnName("TotalCurrency")
                .HasMaxLength(3).IsRequired();
        });
        builder.Navigation(invoice => invoice.Total).IsRequired();

        builder.Property(invoice => invoice.IsIssued).IsRequired();

        // The idempotency query. DraftInvoiceHandler looks for an existing invoice before
        // creating one, because WorkOrderCompletedV1 can be delivered more than once -- and
        // unique, because a work order having two invoices is not a state worth representing.
        builder.HasIndex(invoice => invoice.WorkOrderId)
            .HasDatabaseName("IX_Invoices_WorkOrder")
            .IsUnique();

        builder.Ignore(invoice => invoice.DomainEvents);
    }
}

public static class BillingPersistence
{
    public static IServiceCollection AddBillingPersistence(
        this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<BillingDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(6, TimeSpan.FromSeconds(20), null);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", BillingDbContext.Schema);
            }));

        return services;
    }
}

/// <summary>
/// The invoice repository, backed by EF Core.
/// </summary>
public sealed class EfInvoiceRepository(BillingDbContext db) : IInvoiceRepository
{
    public async Task<Invoice?> GetByWorkOrderAsync(Guid workOrderId, CancellationToken ct = default) =>
        await db.Invoices.FirstOrDefaultAsync(invoice => invoice.WorkOrderId == workOrderId, ct);

    public async Task AddAsync(Invoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        await db.Invoices.AddAsync(invoice, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <remarks>
    /// Unbounded, and it should not stay that way. It backs <c>GET /api/invoices</c>, which is a
    /// demonstration endpoint rather than a real query -- the first person to run this against a
    /// year of invoices will fetch all of them. Paging belongs with the read models on
    /// build-plan day 7, where the query side gets designed rather than improvised.
    /// </remarks>
    public async Task<IReadOnlyList<Invoice>> ListAsync(CancellationToken ct = default) =>
        await db.Invoices.OrderBy(invoice => invoice.Id).ToListAsync(ct);
}
