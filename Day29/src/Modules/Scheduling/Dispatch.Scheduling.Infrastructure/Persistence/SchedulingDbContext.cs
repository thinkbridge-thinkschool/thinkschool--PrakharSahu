using Dispatch.Scheduling.Domain.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Scheduling.Infrastructure.Persistence;

/// <summary>
/// The Scheduling module's own database context, over the <c>scheduling</c> schema.
/// </summary>
public sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options)
    : DbContext(options)
{
    public const string Schema = "scheduling";

    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SchedulingDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// How a <see cref="Reservation"/> is stored.
/// </summary>
/// <remarks>
/// Simpler than the work order: no owned collections, and the only value object is the technician
/// id. Note that Scheduling's <c>TechnicianId</c> is a <c>sealed record</c> while
/// WorkManagement's is a <c>readonly record struct</c> — the same name, two independent types,
/// which is the bounded-context boundary showing up as a mapping difference rather than as a
/// shared class.
/// </remarks>
public sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("Reservations");

        builder.HasKey(reservation => reservation.Id);

        builder.Property(reservation => reservation.Id)
            .HasConversion(id => id.Value, value => new ReservationId(value))
            .ValueGeneratedNever();

        builder.Property(reservation => reservation.TechnicianId)
            .HasConversion(id => id.Value, value => new TechnicianId(value))
            .IsRequired();

        // A plain Guid, not a WorkOrderId. Scheduling holds a reference to something in another
        // context and deliberately does not import its id type -- that would be a compile-time
        // dependency on WorkManagement's internals for the sake of type safety Scheduling cannot
        // enforce anyway.
        builder.Property(reservation => reservation.WorkOrderId).IsRequired();

        builder.Property(reservation => reservation.Start).IsRequired();
        builder.Property(reservation => reservation.End).IsRequired();
        builder.Property(reservation => reservation.IsReleased).IsRequired();

        // The query the overlap check runs. Filtered on live reservations, because a released one
        // can never conflict and there is no reason to keep it in the index.
        builder.HasIndex(reservation => new { reservation.TechnicianId, reservation.Start, reservation.End })
            .HasDatabaseName("IX_Reservations_Technician_Window")
            .HasFilter("[IsReleased] = 0");

        // Answers "is this order already booked?", which the handler checks for idempotency.
        builder.HasIndex(reservation => reservation.WorkOrderId)
            .HasDatabaseName("IX_Reservations_WorkOrder");

        builder.Ignore(reservation => reservation.DomainEvents);
    }
}

public static class SchedulingPersistence
{
    public static IServiceCollection AddSchedulingPersistence(
        this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<SchedulingDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(6, TimeSpan.FromSeconds(20), null);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", SchedulingDbContext.Schema);
            }));

        return services;
    }
}
