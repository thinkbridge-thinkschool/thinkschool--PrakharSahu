using Dispatch.WorkManagement.Domain.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dispatch.WorkManagement.Infrastructure.Persistence;

/// <summary>
/// How the <see cref="WorkOrder"/> aggregate is stored.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate was designed before any database existed: private setters, a private
/// constructor, value objects with factory methods that return <c>Result</c>, and a labour
/// collection exposed only as <c>IReadOnlyList</c>. None of that was bent to suit EF, and this
/// file is where the cost of that shows up — it is longer than a naive mapping would be, and the
/// domain stayed clean in exchange.
/// </para>
/// <para>
/// The aggregate boundary is mapped as a boundary. Labour entries are an owned collection, so
/// they load with the order and cannot be queried or written independently; the technician and
/// the customer are ids, so nothing pulls their rows into a work order's transaction.
/// </para>
/// </remarks>
public sealed class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public void Configure(EntityTypeBuilder<WorkOrder> builder)
    {
        builder.ToTable("WorkOrders");

        // --------------------------------------------------------------------------------------
        // Identity. The domain uses `readonly record struct` wrappers rather than bare Guids, so
        // a CustomerId can never be passed where a TechnicianId is expected. That type safety is
        // free at runtime and has to be unwrapped here.
        //
        // ValueGeneratedNever, because WorkOrderId.New() creates a v7 GUID in the domain. Letting
        // the database generate it would mean an aggregate is not fully formed until it is saved,
        // and `Raise` would have nothing to put in the event it publishes.
        // --------------------------------------------------------------------------------------
        builder.HasKey(order => order.Id);

        builder.Property(order => order.Id)
            .HasConversion(id => id.Value, value => new WorkOrderId(value))
            .ValueGeneratedNever();

        builder.Property(order => order.CustomerId)
            .HasConversion(id => id.Value, value => new CustomerId(value))
            .IsRequired();

        builder.Property(order => order.AssignedTechnicianId)
            .HasConversion(
                id => id!.Value.Value,
                value => new TechnicianId(value));

        // --------------------------------------------------------------------------------------
        // Scalars.
        //
        // The enums are stored as STRINGS. An int column saves three bytes and costs you the
        // ability to read the table: `status = 3` means nothing during an incident, and renumbering
        // the enum silently rewrites history. `'InProgress'` is self-describing in a query window
        // and survives someone reordering the enum members.
        // --------------------------------------------------------------------------------------
        builder.Property(order => order.Summary)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(order => order.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(order => order.Priority)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(order => order.CancellationReason)
            .HasMaxLength(500);

        // --------------------------------------------------------------------------------------
        // Value objects, owned. They have no identity of their own and no lifetime apart from the
        // order, which is exactly what OwnsOne models.
        //
        // Both have private constructors taking their properties, so EF binds through those
        // rather than needing setters. The factory methods stay the only public way to build one,
        // which keeps validation unavoidable for application code while letting EF rehydrate rows
        // that were already validated on the way in.
        // --------------------------------------------------------------------------------------
        builder.OwnsOne(order => order.Address, address =>
        {
            address.Property(a => a.Line).HasColumnName("AddressLine")
                .HasMaxLength(ServiceAddress.MaxLineLength).IsRequired();
            address.Property(a => a.City).HasColumnName("AddressCity")
                .HasMaxLength(100).IsRequired();
            address.Property(a => a.Postcode).HasColumnName("AddressPostcode")
                .HasMaxLength(20).IsRequired();
        });
        builder.Navigation(order => order.Address).IsRequired();

        // Nullable: a window exists only once the order has been scheduled.
        builder.OwnsOne(order => order.Window, window =>
        {
            window.Property(w => w.Start).HasColumnName("WindowStart");
            window.Property(w => w.End).HasColumnName("WindowEnd");
        });

        // --------------------------------------------------------------------------------------
        // Labour, an owned collection reached through a backing field.
        //
        // The aggregate exposes `IReadOnlyList<LabourEntry> Labour => _labour` and mutates the
        // list only inside LogLabour, so there is no setter for EF to use. UsePropertyAccessMode
        // Field tells it to read and write `_labour` directly, which is what keeps the
        // encapsulation intact: application code still cannot add an entry without going through
        // the method that enforces "labour only while InProgress".
        //
        // OwnsMany rather than HasMany, because a labour entry is part of the order. It has an
        // Id so EF can track it, but it is not an aggregate — nothing outside may load one,
        // change one, or delete one on its own.
        // --------------------------------------------------------------------------------------
        builder.OwnsMany(order => order.Labour, labour =>
        {
            labour.ToTable("WorkOrderLabour");
            labour.WithOwner().HasForeignKey("WorkOrderId");
            labour.HasKey(entry => entry.Id);

            labour.Property(entry => entry.Id).ValueGeneratedNever();

            labour.Property(entry => entry.TechnicianId)
                .HasConversion(id => id.Value, value => new TechnicianId(value))
                .IsRequired();

            labour.Property(entry => entry.Minutes).IsRequired();
            labour.Property(entry => entry.Note).HasMaxLength(500).IsRequired();
        });

        builder.Navigation(order => order.Labour)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // --------------------------------------------------------------------------------------
        // The one index the application actually queries by.
        //
        // SlaSweeper asks for open orders past their due date, every minute, forever. Filtered on
        // the statuses that can still breach, so the index stays proportional to the OPEN work
        // rather than to every order ever raised — the table grows without bound and the open set
        // should not.
        //
        // Domain events are not persisted. They are raised, dispatched and dropped within the
        // request; the outbox that makes them durable is build-plan day 3.
        // --------------------------------------------------------------------------------------
        builder.HasIndex(order => new { order.Status, order.DueBy })
            .HasDatabaseName("IX_WorkOrders_Open_DueBy")
            .HasFilter("[Status] IN ('Raised', 'Triaged', 'Scheduled', 'InProgress')");

        builder.Ignore(order => order.DomainEvents);
    }
}
