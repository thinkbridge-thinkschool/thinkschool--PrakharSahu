using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface IQuoteRepository
{
    Task<IEnumerable<Quote>> GetAllAsync(CancellationToken ct = default);
    Task<Quote?> GetByIdAsync(int id, CancellationToken ct = default);
    Task AddAsync(Quote quote, CancellationToken ct = default);
    Task UpdateAsync(Quote quote, CancellationToken ct = default);
    Task DeleteAsync(Quote quote, CancellationToken ct = default);
}
