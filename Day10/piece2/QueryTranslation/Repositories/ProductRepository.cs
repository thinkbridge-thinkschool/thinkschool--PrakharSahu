using Microsoft.EntityFrameworkCore;
using QueryTranslation.Data;
using QueryTranslation.Models;

namespace QueryTranslation.Repositories;

// The two accessors below differ by one word in the return type, and that one word decides
// whether a caller's .Where() becomes a SQL WHERE clause or a foreach over the whole table.
public class ProductRepository
{
    private readonly AppDbContext _context;

    public ProductRepository(AppDbContext context)
    {
        _context = context;
    }

    // The trap. Returning IEnumerable<Product> means any .Where() a caller chains on binds to
    // Enumerable.Where instead of Queryable.Where, so EF has already been asked for every row
    // by then. Nothing warns you: it compiles, it returns correct results, it is just slow.
    public IEnumerable<Product> GetAllAsEnumerable() => _context.Products.AsNoTracking();

    // The fix is only that the composition stays in the IQueryable world until it is enumerated.
    public IQueryable<Product> GetAllAsQueryable() => _context.Products.AsNoTracking();
}
