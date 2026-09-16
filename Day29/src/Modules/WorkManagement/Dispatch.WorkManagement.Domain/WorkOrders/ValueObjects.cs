using Dispatch.SharedKernel;

namespace Dispatch.WorkManagement.Domain.WorkOrders;

/// <summary>The lifecycle of a work order. Every transition is a method on the aggregate.</summary>
/// <remarks>
/// <para>
/// Modelled as an explicit state rather than a spread of booleans (<c>IsScheduled</c>,
/// <c>IsComplete</c>, <c>IsCancelled</c>). Three booleans describe eight states, five of which are
/// nonsense — "cancelled and complete" is representable and meaningless. One enum describes
/// exactly the states that exist.
/// </para>
/// <para>
/// The transitions:
/// <code>
///   Raised --triage--> Triaged --schedule--> Scheduled --start--> InProgress --complete--> Completed
///                         ^                      |
///                         +---returnToTriage-----+   (the scheduling reservation failed)
///
///   any of the above except Completed --cancel--> Cancelled
/// </code>
/// </para>
/// </remarks>
public enum WorkOrderStatus
{
    /// <summary>Reported, not yet assessed.</summary>
    Raised = 0,

    /// <summary>Assessed: priority set and an SLA due date derived from it.</summary>
    Triaged = 1,

    /// <summary>A technician and a time window have been committed to.</summary>
    Scheduled = 2,

    /// <summary>Work has begun on site.</summary>
    InProgress = 3,

    /// <summary>Work is finished and the order is billable. Terminal.</summary>
    Completed = 4,

    /// <summary>Abandoned before completion. Terminal, and never billable.</summary>
    Cancelled = 5
}

/// <summary>How urgent the work is. Set at triage, and the sole input to the SLA due date.</summary>
public enum WorkOrderPriority
{
    Low = 0,
    Standard = 1,
    High = 2,

    /// <summary>Safety or total loss of service.</summary>
    Emergency = 3
}

/// <summary>
/// Where the work happens.
/// </summary>
/// <remarks>
/// A value object: two addresses with the same fields <em>are</em> the same address, so it is
/// compared by value and has no id. Modelled as a record for exactly that reason.
///
/// Validation lives in <see cref="Create"/> and the constructor is private, so an invalid address
/// cannot be constructed anywhere in the system — including by a test, a deserialiser or a
/// well-meaning mapper.
/// </remarks>
public sealed record ServiceAddress
{
    public const int MaxLineLength = 200;

    private ServiceAddress(string line, string city, string postcode)
    {
        Line = line;
        City = city;
        Postcode = postcode;
    }

    public string Line { get; }
    public string City { get; }
    public string Postcode { get; }

    public static Result<ServiceAddress> Create(string? line, string? city, string? postcode)
    {
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(postcode))
        {
            return Result.Failure<ServiceAddress>(
                new Error("address.incomplete", "An address needs a line, a city and a postcode."));
        }

        if (line.Length > MaxLineLength)
        {
            return Result.Failure<ServiceAddress>(
                new Error("address.line_too_long", $"The address line cannot exceed {MaxLineLength} characters."));
        }

        return new ServiceAddress(line.Trim(), city.Trim(), postcode.Trim().ToUpperInvariant());
    }

    public override string ToString() => $"{Line}, {City} {Postcode}";
}

/// <summary>
/// The agreed time window for a visit.
/// </summary>
/// <remarks>
/// A window rather than a single timestamp because that is what is actually promised to a
/// customer — "someone will arrive between 9 and 11". Modelling it as a start time and hoping
/// everyone remembers the implied duration is how a scheduling system ends up double-booking.
/// </remarks>
public sealed record ScheduledWindow
{
    private ScheduledWindow(DateTimeOffset start, DateTimeOffset end)
    {
        Start = start;
        End = end;
    }

    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }

    public TimeSpan Duration => End - Start;

    public static Result<ScheduledWindow> Create(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
    {
        if (end <= start)
        {
            return Result.Failure<ScheduledWindow>(
                new Error("window.inverted", "A scheduled window must end after it starts."));
        }

        // Scheduling into the past is always a bug — a clock skew, a timezone mistake, or a stale
        // form. Rejecting it here means the rest of the aggregate can trust that a scheduled
        // window is something that has not happened yet.
        if (start < now)
        {
            return Result.Failure<ScheduledWindow>(
                new Error("window.in_the_past", "A scheduled window cannot start in the past."));
        }

        return new ScheduledWindow(start, end);
    }

    public bool HasOpenedBy(DateTimeOffset instant) => instant >= Start;
}

/// <summary>
/// Time a technician spent on the job.
/// </summary>
/// <remarks>
/// <para>
/// An entity inside the aggregate, not an aggregate of its own. It has an identity (you can
/// correct one entry without touching the others) but it has no meaning outside its work order,
/// and no one will ever load a labour entry on its own.
/// </para>
/// <para>
/// This is also what makes the "cannot complete with no labour logged" rule enforceable in one
/// transaction: the entries are inside the boundary, so the aggregate can count them without a
/// query.
/// </para>
/// </remarks>
public sealed class LabourEntry : Entity<Guid>
{
    private LabourEntry(Guid id, TechnicianId technicianId, int minutes, string note) : base(id)
    {
        TechnicianId = technicianId;
        Minutes = minutes;
        Note = note;
    }

    private LabourEntry() { }   // EF

    public TechnicianId TechnicianId { get; private set; }
    public int Minutes { get; private set; }
    public string Note { get; private set; } = string.Empty;

    internal static Result<LabourEntry> Create(TechnicianId technicianId, int minutes, string? note)
    {
        if (minutes <= 0)
        {
            return Result.Failure<LabourEntry>(
                new Error("labour.not_positive", "Logged labour must be greater than zero minutes."));
        }

        // A single entry longer than a day is a data-entry slip — someone typed hours into a
        // minutes box, or forgot to stop a timer. It is cheap to reject and expensive to invoice.
        if (minutes > 24 * 60)
        {
            return Result.Failure<LabourEntry>(
                new Error("labour.implausible", "A single labour entry cannot exceed 24 hours."));
        }

        return new LabourEntry(Guid.CreateVersion7(), technicianId, minutes, note?.Trim() ?? string.Empty);
    }
}
