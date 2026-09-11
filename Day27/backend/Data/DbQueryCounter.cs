using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace QuotesApi.Data;

/// <summary>Counts SQL commands actually sent to the database.</summary>
public sealed class DbQueryCounter
{
    private long _commands;

    public void Increment() => Interlocked.Increment(ref _commands);
    public long Commands => Interlocked.Read(ref _commands);
    public void Reset() => Interlocked.Exchange(ref _commands, 0);
}

/// <summary>
/// Increments <see cref="DbQueryCounter"/> for every command EF executes.
/// </summary>
/// <remarks>
/// <para>
/// The measurement the exercise asks for is "DB queries/sec", and the only trustworthy place to
/// count them is where EF hands the command to the provider. Counting at the repository would
/// miss anything EF issues on its own, and counting requests would miss the point entirely —
/// the whole claim of a cache is that requests and queries stop being the same number.
/// </para>
/// <para>
/// Registered for the whole app, so the outbox relay's own sweeps are counted too. That is
/// deliberate: the relay polls on a timer whether or not anyone is loading the site, and its
/// queries are part of the database's real load. The load-test script resets the counter
/// immediately before each run so that background noise is bounded and visible rather than
/// silently folded into the result.
/// </para>
/// </remarks>
public sealed class DbQueryCounterInterceptor : DbCommandInterceptor
{
    private readonly DbQueryCounter _counter;

    public DbQueryCounterInterceptor(DbQueryCounter counter) => _counter = counter;

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        _counter.Increment();
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _counter.Increment();
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        _counter.Increment();
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        _counter.Increment();
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}
