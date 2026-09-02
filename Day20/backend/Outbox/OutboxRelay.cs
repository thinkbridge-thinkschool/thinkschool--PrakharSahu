using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Models;

namespace QuotesApi.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>How often to look for pending rows when the last sweep found nothing.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Rows claimed per sweep.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// How long a claim is held before another relay may take the row.
    /// </summary>
    /// <remarks>
    /// Must exceed the worst realistic publish time. Too short and two relays publish the same
    /// row concurrently — survivable, since the consumer dedupes, but pure waste. Too long and
    /// a crashed relay's rows sit untouched until the lease expires.
    /// </remarks>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts before the row is left alone for a human.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Base for exponential backoff between attempts.</summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(2);

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Publishes outbox rows and marks them sent.
/// </summary>
/// <remarks>
/// <para><b>The order of operations is the guarantee.</b> Claim, publish, then mark. Every
/// crash point in that sequence leaves the system recoverable:</para>
/// <list type="bullet">
///   <item>Crash after claiming, before publishing → the lease expires and the row is picked
///   up again. Not lost.</item>
///   <item>Crash after publishing, before marking → the row is still pending, so it is
///   published a second time. Not lost; <b>duplicated</b>, and absorbed by the consumer's
///   dedupe on the message id.</item>
///   <item>Crash after marking → the row is done and skipped forever. Correct.</item>
/// </list>
/// <para>
/// Marking before publishing would invert the risk: a crash in between loses the message
/// permanently while the row claims it was sent. Losing a message is unrecoverable; sending
/// one twice is a problem the consumer already solves. The pattern trades the unrecoverable
/// failure for the recoverable one, and that trade is the entire design.
/// </para>
/// </remarks>
public sealed class OutboxRelay : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxOptions _options;
    private readonly OutboxFaults _faults;
    private readonly ILogger<OutboxRelay> _logger;

    /// <summary>Identifies this relay in the lease column. Diagnostic, not a correctness input.</summary>
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Consecutive failed sweeps before the log level escalates to Critical.</summary>
    private const int FailureEscalationThreshold = 3;

    public OutboxRelay(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxOptions> options,
        OutboxFaults faults,
        ILogger<OutboxRelay> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _faults = faults;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("Outbox relay is disabled. Events will accumulate unpublished.");
            return;
        }

        _logger.LogInformation(
            "Outbox relay {InstanceId} started. Batch {Batch}, poll {Poll}, lease {Lease}.",
            _instanceId, _options.BatchSize, _options.PollInterval, _options.LeaseDuration);

        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                published = await SweepAsync(stoppingToken);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure)
            {
                // The loop must survive anything a single sweep throws. Letting it escape
                // stops the relay for the life of the process, and the outbox silently fills.
                //
                // But swallowing forever is its own failure mode, and it is worse because it
                // is invisible: a relay whose every sweep throws looks perfectly healthy from
                // outside while delivering nothing. That is exactly what a mistranslated LINQ
                // query did during development — the loop spun, logged at Error, and the
                // outbox never drained. So repeated failure escalates rather than repeating
                // the same line at the same level.
                consecutiveFailures++;

                if (consecutiveFailures >= FailureEscalationThreshold)
                {
                    _logger.LogCritical(
                        failure,
                        "Outbox relay has failed {Count} consecutive sweeps and is delivering NOTHING. "
                        + "Pending messages are accumulating.",
                        consecutiveFailures);
                }
                else
                {
                    _logger.LogError(failure, "Outbox sweep failed. Retrying after the poll interval.");
                }
            }

            // Only idle when there was nothing to do. A full batch means there is probably
            // more waiting, and sleeping through a backlog is how a relay falls behind and
            // never catches up.
            if (published < _options.BatchSize)
            {
                try { await Task.Delay(_options.PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("Outbox relay {InstanceId} stopped.", _instanceId);
    }

    /// <summary>Claims a batch, publishes each row, marks each result. Returns rows attempted.</summary>
    private async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var now = DateTime.UtcNow;
        var lease = now.Add(_options.LeaseDuration);

        // ---------------------------------------------------------------------------------
        // Claim first, in its own transaction.
        //
        // Reading rows and publishing them without claiming lets two relay instances publish
        // the same row at the same moment. The consumer would dedupe it, so nothing breaks —
        // but it doubles broker traffic for no benefit, and it makes the logs impossible to
        // reason about during an incident.
        //
        // The lease is time-bound rather than a lock, so a relay that dies holding claims
        // releases them by doing nothing at all.
        // ---------------------------------------------------------------------------------
        // COALESCE rather than `x == null || x <= now`.
        //
        // The null-check form does not translate: EF flattens it into a mixed &&/|| tree over
        // nullable DateTimeOffset columns that the SQLite provider rejects with "could not be
        // translated". The failure is nastier than it sounds — the sweep throws, the loop's
        // catch logs it and carries on, and the outbox never drains while looking perfectly
        // healthy from the outside. `?? DateTimeOffset.MinValue` becomes a plain COALESCE and
        // says the same thing: a row that has never been deferred or leased is eligible now.
        var maxAttempts = _options.MaxAttempts;

        var claimable = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null
                        && (m.NextAttemptAt ?? DateTime.MinValue) <= now
                        && (m.LockedUntil ?? DateTime.MinValue) <= now
                        && m.Attempts < maxAttempts)
            .OrderBy(m => m.OccurredAt)          // oldest first, so order is mostly preserved
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (claimable.Count == 0) return 0;

        foreach (var message in claimable)
        {
            message.LockedUntil = lease;
            message.LockedBy = _instanceId;
        }
        await db.SaveChangesAsync(cancellationToken);

        var attempted = 0;

        foreach (var message in claimable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;

            // Injected crash: the relay dies with the row still pending. On restart it is
            // republished — nothing lost, nothing duplicated.
            if (_faults.ShouldFail(OutboxFaultMode.BeforePublish))
            {
                _logger.LogCritical(
                    "FAULT INJECTED: crashing before publishing {MessageId}. The row stays pending.",
                    message.Id);
                throw new InvalidOperationException(
                    $"Injected crash before publishing {message.Id}.");
            }

            try
            {
                if (_faults.ShouldFail(OutboxFaultMode.PublishThrows))
                {
                    throw new InvalidOperationException("Injected publish failure (broker unavailable).");
                }

                await publisher.PublishAsync(new QuoteEvent
                {
                    // The row id IS the message id. Republishing after a crash therefore
                    // reuses it, which is what makes the duplicate recognisable downstream.
                    EventId = message.Id.ToString(),
                    EventType = message.Type,
                    QuoteId = int.TryParse(message.AggregateId, out var quoteId) ? quoteId : 0,
                    Author = ReadString(message.Payload, "Author"),
                    Text = ReadString(message.Payload, "Text"),
                    OccurredAt = new DateTimeOffset(message.OccurredAt, TimeSpan.Zero)
                }, cancellationToken);

                // Injected crash: the broker HAS the message, but this row still says pending.
                // On restart it is published again — the duplicate that makes this
                // at-least-once, and the reason the consumer dedupes.
                if (_faults.ShouldFail(OutboxFaultMode.AfterPublishBeforeMark))
                {
                    _logger.LogCritical(
                        "FAULT INJECTED: published {MessageId}, crashing before marking it sent. "
                        + "It will be published again on restart.",
                        message.Id);
                    throw new InvalidOperationException(
                        $"Injected crash after publishing {message.Id}.");
                }

                // Only now. This is the commit point of the whole pattern.
                message.ProcessedAt = DateTime.UtcNow;
                message.LockedUntil = null;
                message.LockedBy = null;
                message.LastError = null;
                message.Attempts++;

                _logger.LogInformation(
                    "Published outbox message {MessageId} ({Type}) on attempt {Attempt}.",
                    message.Id, message.Type, message.Attempts);
            }
            catch (Exception failure) when (failure.Message.Contains("Injected crash after publishing"))
            {
                // Deliberately left pending and unsaved, exactly as a hard crash would.
                throw;
            }
            catch (Exception failure)
            {
                message.Attempts++;
                message.LastError = failure.Message;
                message.LockedUntil = null;
                message.LockedBy = null;

                // Exponential backoff. Retrying a broker outage every two seconds is how a
                // transient failure becomes a self-inflicted denial of service.
                var delay = TimeSpan.FromMilliseconds(
                    _options.RetryBackoff.TotalMilliseconds * Math.Pow(2, message.Attempts - 1));
                message.NextAttemptAt = DateTime.UtcNow.Add(delay);

                if (message.Attempts >= _options.MaxAttempts)
                {
                    // Left pending on purpose rather than marked failed. The row is the only
                    // record that this event ever needed to be sent; deleting or tombstoning
                    // it discards the evidence required to replay it once the cause is fixed.
                    _logger.LogError(
                        failure,
                        "Outbox message {MessageId} has failed {Attempts} times and will not be retried "
                        + "automatically. It remains pending for replay.",
                        message.Id, message.Attempts);
                }
                else
                {
                    _logger.LogWarning(
                        "Outbox message {MessageId} failed on attempt {Attempt}; retrying in {Delay}.",
                        message.Id, message.Attempts, delay);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return attempted;
    }

    /// <summary>
    /// Pulls one property out of the stored payload.
    /// </summary>
    /// <remarks>
    /// Tolerant by design. The payload was serialised by a possibly older version of the app,
    /// and a relay that throws on an unexpected shape would block the whole queue behind one
    /// row it cannot read.
    /// </remarks>
    private static string ReadString(string payload, string property)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty(property, out var value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }
}
