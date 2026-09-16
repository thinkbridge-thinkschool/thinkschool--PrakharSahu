using Dispatch.SharedKernel;

namespace Dispatch.WorkManagement.Contracts;

// ==============================================================================================
// WorkManagement's published contract. The ONLY thing other modules are allowed to reference.
//
// Three rules hold everywhere in this file, and they are what make the boundary real:
//
//   1. PRIMITIVES ONLY. Guid, string, int, DateTimeOffset. No WorkOrderId, no ServiceAddress, no
//      WorkOrderStatus. Those are internal types, and putting one here would mean every
//      subscriber recompiles when WorkManagement renames a field -- which is exactly the coupling
//      the module boundary exists to prevent.
//
//   2. VERSIONED IN THE NAME. "V1" is not decoration. A published event has subscribers that
//      deploy on their own schedule, so a breaking change is a NEW event alongside the old one,
//      never an edit to this one. The suffix makes that visible at the call site.
//
//   3. ONLY WHAT SOMEBODY NEEDS. WorkManagement raises seven domain events internally; three
//      appear here. The other four are nobody else's business, and publishing them "just in
//      case" would create subscribers this module then has to keep working forever.
// ==============================================================================================

/// <summary>
/// A work order has been assigned a technician and a time window.
/// </summary>
/// <remarks>
/// Consumed by Scheduling, which tries to reserve the technician's calendar. Note what this
/// event does <em>not</em> say: it does not claim the technician is available. WorkManagement
/// has no way to know that — it is asserting intent, and Scheduling gets to disagree.
/// </remarks>
public sealed record WorkOrderScheduledV1(
    Guid WorkOrderId,
    Guid TechnicianId,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd) : IntegrationEvent;

/// <summary>
/// A work order is finished and billable.
/// </summary>
/// <remarks>
/// Consumed by Billing. Carries the labour total so Billing never has to call back into
/// WorkManagement to price the job — a query across a module boundary is a synchronous coupling
/// wearing an asynchronous costume, and it fails the moment the two are deployed separately.
/// </remarks>
public sealed record WorkOrderCompletedV1(
    Guid WorkOrderId,
    Guid CustomerId,
    int TotalLabourMinutes,
    bool WithinSla,
    DateTimeOffset CompletedAt) : IntegrationEvent;

/// <summary>
/// A work order will not go ahead, in the form the last schedule described.
/// </summary>
/// <remarks>
/// Covers both cancellation and returning to triage, because from Scheduling's point of view they
/// are the same instruction: release the slot. Two events would mean two handlers doing identical
/// work, and one of them eventually being forgotten.
/// </remarks>
public sealed record WorkOrderReleasedV1(
    Guid WorkOrderId,
    string Reason) : IntegrationEvent;
