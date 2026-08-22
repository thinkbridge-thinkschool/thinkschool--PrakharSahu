using MediatR;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;

namespace QuotesApi.CQRS.Queries;

// The Query (Request for data)
public record GetQuotesQuery() : IRequest<List<QuoteReadModel>>;

// The Read Model (Shape of the data) - updated for Dapper/SQLite compatibility
public record QuoteReadModel
{
    public int Id { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public int UserId { get; init; }
}

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
            .Select(q => new QuoteReadModel 
            { 
                Id = q.Id, 
                Author = q.Author, 
                Text = q.Text, 
                UserId = q.UserId 
            })
            .ToListAsync(ct);
    }
}