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

    /// <summary>
    /// The duplicate check is scoped to the author on purpose: two different authors saying the
    /// same words is a real thing (and worth recording), while the same author saying it twice
    /// is just a double post.
    ///
    /// Trim + ToLower translate to SQLite's own trim()/lower(), so the comparison happens in the
    /// database rather than by pulling every quote into memory. Note that SQLite's lower() only
    /// folds ASCII, so an accented capital would be treated as a different author here.
    ///
    /// The analyzer will suggest string.Equals(..., StringComparison.OrdinalIgnoreCase) instead.
    /// Do not take it: EF Core cannot translate the StringComparison overloads, so the query
    /// would fall back to client-side evaluation over the whole table.
    /// </summary>
    public async Task<bool> ExistsForAuthorAsync(string author, string text, int? excludingId = null, CancellationToken ct = default)
    {
        var normalisedAuthor = author.Trim().ToLower();
        var normalisedText = text.Trim().ToLower();

        return await _db.Quotes
            .AsNoTracking()
            .Where(q => !q.IsDeleted)
            .Where(q => excludingId == null || q.Id != excludingId)
            .AnyAsync(
                q => q.Author.Trim().ToLower() == normalisedAuthor
                  && q.Text.Trim().ToLower() == normalisedText,
                ct);
    }

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
