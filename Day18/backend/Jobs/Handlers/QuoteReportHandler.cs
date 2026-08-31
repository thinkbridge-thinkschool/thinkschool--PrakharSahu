using System.Text;
using QuotesApi.Models;
using QuotesApi.Repositories;

namespace QuotesApi.Jobs.Handlers;

/// <summary>
/// Builds a per-author summary of every quote. The realistic slow job: a full read, some
/// work per row, and a result nobody wants to wait for on a request thread.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IQuoteRepository"/> is <b>scoped</b>, and this handler can depend on it only
/// because <see cref="JobProcessor"/> resolves handlers from a fresh scope per job. Inject a
/// scoped service into the singleton processor directly and you get a captive dependency —
/// one DbContext shared by every job for the life of the process, which fails in ways that
/// look like data corruption long before they look like a DI mistake.
/// </para>
/// <para>
/// The token is threaded into every await, including the artificial delay. That is the
/// contract from <see cref="IJobHandler"/>: it is what makes the job cancellable by
/// <c>DELETE /api/jobs/{id}</c> and what lets the host shut down promptly instead of waiting
/// out its timeout.
/// </para>
/// </remarks>
public sealed class QuoteReportHandler : IJobHandler
{
    public const string Type = "quote-report";

    private readonly IQuoteRepository _repository;
    private readonly ILogger<QuoteReportHandler> _logger;

    public QuoteReportHandler(IQuoteRepository repository, ILogger<QuoteReportHandler> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public string JobType => Type;

    /// <summary>
    /// Stands in for genuinely expensive per-row work — rendering, an outbound call, a PDF.
    /// Small enough that the demo is not tedious, long enough that cancellation is observable.
    /// </summary>
    private static readonly TimeSpan PerQuoteCost = TimeSpan.FromMilliseconds(400);

    public async Task<string> HandleAsync(Job job, CancellationToken cancellationToken)
    {
        var quotes = (await _repository.GetAllAsync(cancellationToken)).ToList();

        if (quotes.Count == 0)
        {
            job.Progress = "No quotes to report on.";
            return "0 quotes, 0 authors.";
        }

        var byAuthor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var processed = 0;

        foreach (var quote in quotes)
        {
            // Checked every iteration rather than once at the top. A job that only checks its
            // token before starting is not cancellable — it is merely skippable.
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(PerQuoteCost, cancellationToken);

            byAuthor[quote.Author] = byAuthor.GetValueOrDefault(quote.Author) + 1;
            processed++;

            // Written to the job the caller is polling, so a slow job reports progress
            // instead of looking hung.
            job.Progress = $"Processed {processed} of {quotes.Count} quotes.";
        }

        var summary = new StringBuilder();
        summary.Append(quotes.Count).Append(" quotes across ").Append(byAuthor.Count).Append(" authors. ");
        summary.Append("Top: ");
        summary.AppendJoin(", ", byAuthor
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(pair => $"{pair.Key} ({pair.Value})"));

        _logger.LogInformation(
            "Report job {JobId} summarised {QuoteCount} quotes by {AuthorCount} authors.",
            job.Id, quotes.Count, byAuthor.Count);

        return summary.ToString();
    }
}
