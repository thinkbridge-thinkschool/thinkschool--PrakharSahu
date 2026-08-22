using MediatR;
using Dapper;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;

namespace QuotesApi.CQRS.Queries;

public record GetQuotesDapperQuery() : IRequest<List<QuoteReadModel>>;

public class GetQuotesDapperQueryHandler : IRequestHandler<GetQuotesDapperQuery, List<QuoteReadModel>>
{
    private readonly AppDbContext _db;

    public GetQuotesDapperQueryHandler(AppDbContext db) => _db = db;

    public async Task<List<QuoteReadModel>> Handle(GetQuotesDapperQuery request, CancellationToken ct)
    {
        using var connection = _db.Database.GetDbConnection();
        
        const string sql = @"
            SELECT Id, Author, Text, UserId 
            FROM Quotes 
            WHERE IsDeleted = 0";
            
        var result = await connection.QueryAsync<QuoteReadModel>(sql);
        return result.AsList();
    }
}