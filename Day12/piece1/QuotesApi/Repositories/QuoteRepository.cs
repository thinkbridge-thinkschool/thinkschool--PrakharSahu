using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Repositories;

public class QuoteRepository : IQuoteRepository
{
    private readonly AppDbContext _db;
    public QuoteRepository(AppDbContext db) => _db = db;

    // Notice we now filter out deleted quotes!
    public async Task<IEnumerable<Quote>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Quotes.AsNoTracking().Where(q => !q.IsDeleted).ToListAsync(ct);

    public async Task<Quote?> GetByIdAsync(int id, CancellationToken ct = default) =>
        await _db.Quotes.FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted, ct);

    public async Task AddAsync(Quote quote, CancellationToken ct = default)
    {
        _db.Quotes.Add(quote);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Quote quote, CancellationToken ct = default)
    {
        _db.Quotes.Update(quote);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Quote quote, CancellationToken ct = default)
    {
        _db.Quotes.Update(quote);
        await _db.SaveChangesAsync(ct);
    }
}
