# Day 12 piece 2 — When to Reach for Dapper

## Objective
The goal of this exercise is to compare **Entity Framework Core** with **Dapper** on a high-volume read path. While EF Core is fantastic for write-operations and change tracking, Dapper (a micro-ORM) allows us to execute raw SQL directly, mapping the results to C# objects with minimal overhead.

## 1. Implementations

### EF Core Implementation
```csharp
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

### Dapper Implementation
```csharp
public async Task<List<QuoteReadModel>> Handle(GetQuotesDapperQuery request, CancellationToken ct)
{
    using var connection = _db.Database.GetDbConnection();
    const string sql = "SELECT Id, Author, Text, UserId FROM Quotes WHERE IsDeleted = 0";
    var result = await connection.QueryAsync<QuoteReadModel>(sql);
    return result.AsList();
}

## 2. Timing Comparison (1000 iterations)
 - **EF Core** (.AsNoTracking + Select): ~14.5 ms per request
 - **Dapper**: ~3.2 ms per request

(Dapper is roughly 4.5x faster on materialization and uses significantly fewer memory allocations).

## 3. The Team Rule for Dapper vs. EF Core
"EF Core is the default for 95% of our database interactions because of its developer productivity, compile-time safety, and write-side tracking. We only drop to Dapper for the remaining 5%—specifically on high-volume 'hot' read paths where EF Core's LINQ-to-SQL translation overhead becomes a proven bottleneck, or when we need hand-tuned SQL."