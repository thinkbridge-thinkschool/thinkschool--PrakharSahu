using Dispatch.SharedKernel;

namespace Dispatch.Billing.Domain.Invoices;

public readonly record struct InvoiceId(Guid Value)
{
    public static InvoiceId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

/// <summary>
/// An amount of money. Never a bare decimal.
/// </summary>
/// <remarks>
/// A decimal alone cannot say whether it is pounds or rupees, and the bug that produces is
/// silent, expensive and discovered by a customer. Pairing the amount with its currency and
/// refusing to add across currencies makes the mistake a compile-time or fail-fast one instead.
/// </remarks>
public sealed record Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency) => new(0m, currency);

    public static Money operator +(Money left, Money right) =>
        left.Currency == right.Currency
            ? new Money(left.Amount + right.Amount, left.Currency)
            : throw new InvalidOperationException(
                $"Cannot add {left.Currency} to {right.Currency}. Convert first, explicitly.");

    public override string ToString() => $"{Amount:0.00} {Currency}";
}

/// <summary>
/// A draft bill for one completed work order.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate root of the Billing context, and it is a genuinely separate one -- not a
/// property hanging off a work order. Invoices have their own lifecycle (draft, issued, paid,
/// credited) that outlives the job, their own approval rules, and their own audit requirements.
/// </para>
/// <para>
/// It starts as a DRAFT. Billing does not decide unilaterally that a customer owes money the
/// instant an engineer taps "done" on a phone; a human approves it. Modelling the draft state
/// explicitly is what leaves room for that step instead of assuming it away.
/// </para>
/// </remarks>
public sealed class Invoice : AggregateRoot<InvoiceId>
{
    /// <summary>Charged in whole hours, rounded up. A ten-minute callout is not billed as ten minutes.</summary>
    private const decimal HourlyRate = 85m;
    private const string DefaultCurrency = "GBP";

    private Invoice(InvoiceId id, Guid workOrderId, Guid customerId, Money total) : base(id)
    {
        WorkOrderId = workOrderId;
        CustomerId = customerId;
        Total = total;
    }

    private Invoice() { }   // EF

    public Guid WorkOrderId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Money Total { get; private set; } = Money.Zero(DefaultCurrency);
    public bool IsIssued { get; private set; }

    /// <summary>
    /// Prices a completed job.
    /// </summary>
    /// <remarks>
    /// The pricing rule lives here, in Billing, and nowhere else. WorkManagement reports minutes;
    /// what a minute costs is not its business, and a work order that knew the hourly rate would
    /// have to be redeployed every time finance changed it.
    /// </remarks>
    public static Result<Invoice> Draft(Guid workOrderId, Guid customerId, int labourMinutes)
    {
        if (labourMinutes <= 0)
        {
            return Result.Failure<Invoice>(
                new Error("invoice.no_labour", "Cannot draft an invoice for a job with no labour."));
        }

        var billableHours = Math.Ceiling(labourMinutes / 60m);
        var total = new Money(billableHours * HourlyRate, DefaultCurrency);

        return new Invoice(InvoiceId.New(), workOrderId, customerId, total);
    }

    public Result Issue()
    {
        if (IsIssued)
        {
            return Result.Failure(new Error("invoice.already_issued", "This invoice has already been issued."));
        }

        IsIssued = true;
        return Result.Success();
    }
}
