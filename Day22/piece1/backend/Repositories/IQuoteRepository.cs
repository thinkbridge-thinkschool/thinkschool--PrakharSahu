using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface IQuoteRepository
{
    Task<IEnumerable<Quote>> GetAllAsync(CancellationToken ct = default);
    Task<Quote?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// True when this author already has a live quote with the same words. Comparison ignores
    /// case and surrounding whitespace; soft-deleted rows do not count. Pass the id of the
    /// quote being edited as <paramref name="excludingId"/> so a row never collides with itself.
    /// </summary>
    Task<bool> ExistsForAuthorAsync(string author, string text, int? excludingId = null, CancellationToken ct = default);
    Task AddAsync(Quote quote, CancellationToken ct = default);
    Task UpdateAsync(Quote quote, CancellationToken ct = default);
    Task DeleteAsync(Quote quote, CancellationToken ct = default);
}
