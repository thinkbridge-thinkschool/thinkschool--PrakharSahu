using MediatR;
using QuotesApi.Models;
using QuotesApi.Data;
using QuotesApi.Services;

namespace QuotesApi.CQRS.Commands;

// The Command (Data packet)
public record CreateQuoteCommand(string Author, string Text, int UserId) : IRequest<Result<Quote>>;

// The Handler (Action)
public class CreateQuoteCommandHandler : IRequestHandler<CreateQuoteCommand, Result<Quote>>
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public CreateQuoteCommandHandler(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Result<Quote>> Handle(CreateQuoteCommand request, CancellationToken ct)
    {
        var quoteResult = Quote.Create(request.Author, request.Text, _clock.UtcNow, request.UserId);
        if (!quoteResult.IsSuccess) return quoteResult;

        _db.Quotes.Add(quoteResult.Value!);
        await _db.SaveChangesAsync(ct);
        return quoteResult;
    }
}