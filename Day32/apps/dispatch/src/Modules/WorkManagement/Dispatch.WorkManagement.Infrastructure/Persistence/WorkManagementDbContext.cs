using Dispatch.WorkManagement.Domain.WorkOrders;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.WorkManagement.Infrastructure.Persistence;

/// <summary>
/// The WorkManagement module's own database context.
/// </summary>
/// <remarks>
/// <para>
/// <b>One context per module, not one shared context.</b> That is the single most important
/// decision in this file, and it is a correction rather than a preference: the Day 27 conversion
/// kept one shared <c>AppDbContext</c> holding every module's entities, which meant every
/// module's Infrastructure transitively saw every other module's tables. It was bounded by an
/// architecture test and it was still the weakest part of that result.
/// </para>
/// <para>
/// Here each module owns a context over its own schema. Scheduling cannot write a work order even
/// by accident, because the type is not reachable from its context. The boundary stops being a
/// rule people have to remember and becomes a thing the compiler enforces.
/// </para>
/// <para>
/// All three contexts point at the <em>same database</em>, separated by schema. That keeps one
/// connection string and one backup story while leaving the split to be made physical later if a
/// module ever needs its own database.
/// </para>
/// </remarks>
public sealed class WorkManagementDbContext(DbContextOptions<WorkManagementDbContext> options)
    : DbContext(options)
{
    public const string Schema = "workmanagement";

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        // Applied from this assembly only. A module must not be able to configure another
        // module's tables, and scanning the whole AppDomain would let it.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkManagementDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
