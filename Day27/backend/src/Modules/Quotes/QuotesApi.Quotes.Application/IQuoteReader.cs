namespace QuotesApi.Quotes.Application;

/// <summary>
/// The shape a quote is cached and served as.
/// </summary>
/// <remarks>
/// A record, not the EF entity, and that is not stylistic. <see cref="Quote"/> has private
/// setters and a private parameterless constructor, so System.Text.Json — HybridCache's default
/// serializer — will happily serialise it and then fail to reconstruct it, yielding objects full
/// of default values rather than an error. Caching a DTO also stops a tracked entity graph from
/// being handed to a second request, which is the other way this goes wrong quietly.
///
/// The six properties match the wire contract Day 16's <c>isQuote()</c> guard checks, so a
/// cached response is byte-identical to an uncached one.
/// </remarks>
public sealed record CachedQuote(
    int Id, string Text, string Author, DateTimeOffset CreatedAt, bool IsDeleted, int UserId)
{
    public static CachedQuote From(Quote quote) =>
        new(quote.Id, quote.Text, quote.Author, quote.CreatedAt, quote.IsDeleted, quote.UserId);
}

public interface IQuoteReader
{
    /// <summary>The hot read: every visitor hits this on the home page.</summary>
    Task<IReadOnlyList<CachedQuote>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Drops the cached list after a write.</summary>
    Task InvalidateAsync(CancellationToken cancellationToken);
}
