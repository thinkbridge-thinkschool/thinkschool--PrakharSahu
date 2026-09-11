using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Outbox;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace QuotesApi.Outbox.Tests;

/// <summary>
/// Writes log entries into a list.
/// </summary>
/// <remarks>
/// The relay deliberately catches everything a sweep throws, so the loop survives a bad batch.
/// With a null logger that safety net also swallows the diagnosis — a sweep failing on every
/// pass looks identical to an empty outbox. This makes the difference visible.
/// </remarks>
public sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly List<string> _sink;
    public CapturingLogger(List<string> sink) => _sink = sink;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_sink)
        {
            _sink.Add($"[{logLevel}] {formatter(state, exception)}"
                      + (exception is null ? "" : $" :: {exception.GetType().Name}: {exception.Message}"));
        }
    }
}

/// <summary>Records what was published, so duplicates are countable.</summary>
public sealed class RecordingPublisher : IEventPublisher
{
    private readonly List<string> _published = new();
    private readonly object _gate = new();

    /// <summary>Set to make publishing fail, as a broker outage would.</summary>
    public Func<QuoteEvent, bool>? FailWhen { get; set; }

    public IReadOnlyList<string> Published
    {
        get { lock (_gate) return _published.ToArray(); }
    }

    /// <summary>Distinct message ids — what an idempotent consumer would actually act on.</summary>
    public IReadOnlyList<string> DistinctPublished
    {
        get { lock (_gate) return _published.Distinct().ToArray(); }
    }

    public Task<string> PublishAsync(QuoteEvent @event, CancellationToken cancellationToken)
    {
        if (FailWhen?.Invoke(@event) == true)
        {
            throw new InvalidOperationException("Broker unavailable (simulated).");
        }

        lock (_gate) _published.Add(@event.EventId);
        return Task.FromResult(@event.EventId);
    }
}

/// <summary>
/// A disposable database plus the services the relay needs.
/// </summary>
/// <remarks>
/// <para><b>SQLite, not the EF InMemory provider.</b> This is not a preference. The InMemory
/// provider does not implement transactions — it accepts <c>BeginTransaction</c> and silently
/// ignores it. Every atomicity test would pass against it while proving nothing, including a
/// test of code that had no transaction at all. SQLite in shared-cache memory mode gives real
/// transactions, real rollbacks and real constraint enforcement.</para>
///
/// <para>The connection is held open for the lifetime of the harness because an in-memory
/// SQLite database exists only as long as a connection to it does; close the last one and the
/// schema disappears mid-test.</para>
/// </remarks>
public sealed class OutboxHarness : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    public ServiceProvider Services { get; }
    public RecordingPublisher Publisher { get; } = new();
    public OutboxFaults Faults { get; } = new();
    public OutboxOptions Options { get; }

    public OutboxHarness(OutboxOptions? options = null)
    {
        Options = options ?? new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(100),
            BatchSize = 10,
            // Short on purpose: a "restarted" relay must be able to re-claim a row the crashed
            // one had leased, without the test waiting out a production-sized lease.
            LeaseDuration = TimeSpan.FromMilliseconds(200),
            MaxAttempts = 5,
            RetryBackoff = TimeSpan.FromMilliseconds(50)
        };

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddSingleton<IEventPublisher>(Publisher);
        services.AddSingleton(Faults);
        services.AddLogging();
        Services = services.BuildServiceProvider();

        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public AppDbContext NewDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    /// <summary>
    /// A relay instance. Creating a second one models a process restart.
    /// </summary>
    /// <summary>Captures relay log output, so a swallowed sweep failure is visible in a test.</summary>
    public static readonly List<string> RelayLog = new();

    public OutboxRelay NewRelay() => new(
        Services.GetRequiredService<IServiceScopeFactory>(),
        MsOptions.Create(Options),
        Faults,
        new CapturingLogger<OutboxRelay>(RelayLog));

    /// <summary>Runs a relay until <paramref name="until"/> holds, or the timeout expires.</summary>
    public async Task<bool> RunRelayUntil(Func<bool> until, TimeSpan timeout)
    {
        var relay = NewRelay();
        await relay.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (until()) return true;
                await Task.Delay(25);
            }
            return until();
        }
        finally
        {
            await relay.StopAsync(CancellationToken.None);
        }
    }

    public int PendingCount()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.OutboxMessages.Count(m => m.ProcessedAt == null);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        _connection.Dispose();
    }
}
