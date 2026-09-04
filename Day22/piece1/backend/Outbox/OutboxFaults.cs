namespace QuotesApi.Outbox;

/// <summary>
/// Where the relay should pretend to die.
/// </summary>
/// <remarks>
/// The two interesting crash points are not symmetrical, and the difference is the whole
/// lesson of the outbox:
/// <list type="bullet">
///   <item><see cref="BeforePublish"/> — the row is still pending, so the relay republishes
///   on restart. <b>Nothing is lost, nothing is duplicated.</b></item>
///   <item><see cref="AfterPublishBeforeMark"/> — the broker already has the message but the
///   row still says pending, so the relay publishes it <b>again</b>. Nothing is lost;
///   something <b>is</b> duplicated. This is the case that makes the guarantee at-least-once
///   rather than exactly-once, and the reason the consumer must be idempotent.</item>
/// </list>
/// </remarks>
public enum OutboxFaultMode
{
    None,

    /// <summary>Throw before handing the message to the broker.</summary>
    BeforePublish,

    /// <summary>Publish, then throw before recording that it was published.</summary>
    AfterPublishBeforeMark,

    /// <summary>Let the publish itself fail, as a broker outage would.</summary>
    PublishThrows
}

/// <summary>
/// Fault injection for the relay, so the crash scenarios can be demonstrated on demand.
/// </summary>
/// <remarks>
/// <para>
/// A singleton toggled at runtime rather than a compile-time flag, because the point is to
/// crash a <em>running</em> relay mid-flight and watch it recover on restart — which cannot be
/// arranged by rebuilding.
/// </para>
/// <para>
/// Gated to Development by the endpoints that set it. An endpoint that can make production
/// drop messages is a liability regardless of how carefully it is named.
/// </para>
/// </remarks>
public sealed class OutboxFaults
{
    private int _remaining;

    public OutboxFaultMode Mode { get; private set; } = OutboxFaultMode.None;

    /// <summary>Arms the fault for the next <paramref name="occurrences"/> messages.</summary>
    public void Arm(OutboxFaultMode mode, int occurrences = 1)
    {
        Mode = mode;
        Interlocked.Exchange(ref _remaining, Math.Max(1, occurrences));
    }

    public void Disarm()
    {
        Mode = OutboxFaultMode.None;
        Interlocked.Exchange(ref _remaining, 0);
    }

    public int Remaining => Volatile.Read(ref _remaining);

    /// <summary>
    /// True when the given point should fail right now. Consumes one occurrence.
    /// </summary>
    /// <remarks>
    /// Self-disarming, so an armed fault does not turn into an infinite crash loop that
    /// prevents the recovery it was meant to demonstrate.
    /// </remarks>
    public bool ShouldFail(OutboxFaultMode at)
    {
        if (Mode != at) return false;
        if (Interlocked.Decrement(ref _remaining) < 0)
        {
            Interlocked.Exchange(ref _remaining, 0);
            Mode = OutboxFaultMode.None;
            return false;
        }
        return true;
    }
}
