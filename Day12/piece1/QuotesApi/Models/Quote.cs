using System;

namespace QuotesApi.Models;

public class Quote
{
    public int Id { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public string Author { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public bool IsDeleted { get; private set; }
    
    // Tracks who created the quote so only they can delete it
    public int UserId { get; private set; }

    private Quote() { }

    private Quote(string author, string text, DateTimeOffset createdAt, int userId)
    {
        Author = author;
        Text = text;
        CreatedAt = createdAt;
        IsDeleted = false;
        UserId = userId;
    }

    public static Result<Quote> Create(string author, string text, DateTimeOffset createdAt, int userId)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            return Result<Quote>.Failure(new DomainError("Text must be between 1 and 1000 characters."));
            
        if (string.IsNullOrWhiteSpace(author) || author.Length > 200)
            return Result<Quote>.Failure(new DomainError("Author must be between 1 and 200 characters."));

        return Result<Quote>.Success(new Quote(author, text, createdAt, userId));
    }

    public Result<bool> ChangeAuthor(string newAuthor)
    {
        if (IsDeleted)
            return Result<bool>.Failure(new DomainError("Cannot modify a deleted quote."));
            
        if (string.IsNullOrWhiteSpace(newAuthor) || newAuthor.Length > 200)
            return Result<bool>.Failure(new DomainError("Author must be between 1 and 200 characters."));
        
        Author = newAuthor;
        return Result<bool>.Success(true);
    }

    public void Delete()
    {
        IsDeleted = true;
    }
}
