using MediatR;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;

namespace QuotesApi.CQRS.Queries;

// The Query (Request for data)
public record GetQuotesQuery() : IRequest<List<QuoteReadModel>>;

// The Read Model (Shape of the data)
public record QuoteReadModel(int Id, string Author, string Text, int UserId);

// The Handler (Data retrieval)
public class GetQuotesQueryHandler : IRequestHandler<GetQuotesQuery, List<QuoteReadModel>>
{
    private readonly AppDbContext _db;

    public GetQuotesQueryHandler(AppDbContext db) => _db = db;

    public async Task<List<QuoteReadModel>> Handle(GetQuotesQuery request, CancellationToken ct)
    {
        return await _db.Quotes
            .AsNoTracking()
            .Where(q => !q.IsDeleted)
            .Select(q => new QuoteReadModel(q.Id, q.Author, q.Text, q.UserId))
            .ToListAsync(ct);
    }
}