using Dispatch.SharedKernel;

namespace Dispatch.Scheduling.Domain.Reservations;

public readonly record struct ReservationId(Guid Value)
{
    public static ReservationId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

/// <summary>
/// A technician, as Scheduling understands one.
/// </summary>
/// <remarks>
/// Worth pausing on. WorkManagement also has a "technician" — and it is a bare
/// <c>TechnicianId</c>, nothing more, because all a work order needs to know is who to blame for
/// the labour. Here a technician has a shift, a skill set and a calendar.
///
/// <b>Two contexts, one word, two entirely different models — and neither is wrong.</b> That is
/// what a bounded context is for. The alternative is a single shared <c>Technician</c> class that
/// carries every field either context ever needed, which satisfies neither and cannot be changed
/// by either without consulting the other.
/// </remarks>
public sealed record TechnicianId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>
/// A block of a technician's calendar, held for one work order.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate root of the Scheduling context. Small on purpose: the invariant it protects is
/// "one technician cannot be in two places at once", and the smallest thing that can enforce it
/// is a single reservation checked against its neighbours.
/// </para>
/// <para>
/// Note that <b>the overlap check is not inside the aggregate</b> — it cannot be. An aggregate
/// can only guarantee rules about data it contains, and this one contains one booking. The rule
/// spans every booking a technician has, so it is enforced by the repository at write time. That
/// is the usual answer for set-wide invariants: an aggregate per booking plus a uniqueness
/// constraint, not one giant "technician calendar" aggregate that serialises every booking in
/// the company through one row.
/// </para>
/// </remarks>
public sealed class Reservation : AggregateRoot<ReservationId>
{
    private Reservation(
        ReservationId id,
        Guid workOrderId,
        TechnicianId technicianId,
        DateTimeOffset start,
        DateTimeOffset end) : base(id)
    {
        WorkOrderId = workOrderId;
        TechnicianId = technicianId;
        Start = start;
        End = end;
    }

    private Reservation() { }   // EF

    /// <summary>The work order this slot is held for, by id. WorkManagement owns the order itself.</summary>
    public Guid WorkOrderId { get; private set; }

    public TechnicianId TechnicianId { get; private set; } = null!;
    public DateTimeOffset Start { get; private set; }
    public DateTimeOffset End { get; private set; }
    public bool IsReleased { get; private set; }

    public static Result<Reservation> Hold(
        Guid workOrderId, TechnicianId technicianId, DateTimeOffset start, DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(technicianId);

        if (end <= start)
        {
            return Result.Failure<Reservation>(
                new Error("reservation.inverted", "A reservation must end after it starts."));
        }

        return new Reservation(ReservationId.New(), workOrderId, technicianId, start, end);
    }

    /// <summary>Gives the slot back. Idempotent, so a redelivered release event is harmless.</summary>
    public void Release() => IsReleased = true;

    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) =>
        !IsReleased && start < End && end > Start;
}
