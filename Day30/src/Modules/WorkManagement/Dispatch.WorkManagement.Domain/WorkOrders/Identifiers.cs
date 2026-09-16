namespace Dispatch.WorkManagement.Domain.WorkOrders;

/// <summary>Identity of a work order.</summary>
/// <remarks>
/// <para>
/// A wrapper around a <see cref="Guid"/> rather than a bare <c>Guid</c>, because this domain has
/// at least four of them — work order, customer, technician, invoice — and bare Guids are
/// mutually assignable. <c>Complete(customerId)</c> where <c>Complete(technicianId)</c> was meant
/// compiles perfectly and fails at runtime with a "not found" that names nothing useful.
/// </para>
/// <para>
/// A <c>readonly record struct</c> so it costs no allocation and gets value equality for free.
/// </para>
/// </remarks>
public readonly record struct WorkOrderId(Guid Value)
{
    public static WorkOrderId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Identity of a customer. The customer itself lives in another context.
/// </summary>
/// <remarks>
/// This module holds the id and nothing more. It has no <c>Customer</c> class, cannot load one,
/// and cannot enforce a rule about one — which is correct: a work order does not own its
/// customer, and pretending otherwise is how a "work order" aggregate quietly grows into the
/// whole system.
/// </remarks>
public readonly record struct CustomerId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Identity of a technician, who belongs to the Scheduling context.
/// </summary>
/// <remarks>
/// Referencing by id across an aggregate boundary is the rule, not a shortcut. A direct object
/// reference would mean loading the technician to save a work order, locking both rows in one
/// transaction, and eventually being unable to change either module without the other. The id is
/// the seam.
/// </remarks>
public readonly record struct TechnicianId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
