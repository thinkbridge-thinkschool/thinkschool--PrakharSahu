using Dispatch.SharedKernel;

namespace Dispatch.Scheduling.Contracts;

/// <summary>The technician's calendar has been blocked out for this work order.</summary>
public sealed record TechnicianReservedV1(
    Guid WorkOrderId,
    Guid TechnicianId,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd) : IntegrationEvent;

/// <summary>
/// The reservation could not be made, and the work order should not believe it is scheduled.
/// </summary>
/// <remarks>
/// The compensating half of the scheduling flow, and the reason it needs one: two modules cannot
/// share a transaction, so WorkManagement commits "scheduled" before Scheduling has had a chance
/// to disagree. This is Scheduling disagreeing, after the fact, in the only way it can.
///
/// Consumed by WorkManagement, which walks its aggregate back to Triaged.
/// </remarks>
public sealed record TechnicianReservationFailedV1(
    Guid WorkOrderId,
    Guid TechnicianId,
    string Reason) : IntegrationEvent;
