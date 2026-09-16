using Dispatch.SharedKernel;

namespace Dispatch.WorkManagement.Domain.WorkOrders;

// ==============================================================================================
// Domain events — internal to WorkManagement.
//
// These name this module's own types (WorkOrderId, Priority, ScheduledWindow) and are free to
// change whenever the model changes, because nothing outside the module ever sees one. The
// Application layer translates the few that matter into integration events at the module edge.
//
// Naming is past tense on purpose. An event is a fact that has already happened and cannot be
// refused; a command is a request that can be. "ScheduleWorkOrder" and "WorkOrderScheduled" are
// different things, and blurring them produces handlers that try to veto history.
// ==============================================================================================

public sealed record WorkOrderRaised(
    WorkOrderId WorkOrderId,
    CustomerId CustomerId,
    string Summary) : DomainEvent;

public sealed record WorkOrderTriaged(
    WorkOrderId WorkOrderId,
    WorkOrderPriority Priority,
    DateTimeOffset DueBy) : DomainEvent;

public sealed record WorkOrderScheduled(
    WorkOrderId WorkOrderId,
    TechnicianId TechnicianId,
    ScheduledWindow Window) : DomainEvent;

/// <summary>
/// Raised when Scheduling could not honour a booking this aggregate had already accepted.
/// </summary>
/// <remarks>
/// The compensating half of the scheduling flow. Two modules cannot share a transaction, so the
/// work order commits "scheduled" before Scheduling has confirmed the slot. When the reservation
/// fails, this walks the state back rather than leaving an order that believes it has a
/// technician who was never actually booked.
/// </remarks>
public sealed record WorkOrderReturnedToTriage(
    WorkOrderId WorkOrderId,
    string Reason) : DomainEvent;

public sealed record WorkOrderStarted(
    WorkOrderId WorkOrderId,
    TechnicianId TechnicianId,
    DateTimeOffset StartedAt) : DomainEvent;

public sealed record WorkOrderCompleted(
    WorkOrderId WorkOrderId,
    CustomerId CustomerId,
    int TotalLabourMinutes,
    bool WithinSla,
    DateTimeOffset CompletedAt) : DomainEvent;

public sealed record WorkOrderCancelled(
    WorkOrderId WorkOrderId,
    string Reason) : DomainEvent;
