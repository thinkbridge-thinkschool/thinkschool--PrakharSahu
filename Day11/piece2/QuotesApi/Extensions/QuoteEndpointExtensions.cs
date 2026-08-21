using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using QuotesApi.Models;
using QuotesApi.Dtos;
using QuotesApi.Repositories;
using QuotesApi.Services;
using QuotesApi.Data;
using Microsoft.EntityFrameworkCore; // Required for ToListAsync

namespace QuotesApi.Extensions;

public static class QuoteEndpointExtensions
{
    public static readonly ActivitySource ActivitySource = new ActivitySource("QuotesApi.Custom");

    public static void MapQuoteEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/quotes");

        group.MapGet("/", async (IQuoteRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.GetAllAsync(ct)));

        group.MapGet("/{id:int}", async (int id, IQuoteRepository repo, CancellationToken ct) =>
            await repo.GetByIdAsync(id, ct) is Quote quote ? Results.Ok(quote) : Results.NotFound());

        group.MapPost("/", async (CreateQuoteRequest request, IQuoteRepository repo, IClock clock, ClaimsPrincipal user, ILogger<Program> logger, CancellationToken ct) =>
        {
            var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
            int.TryParse(userIdClaim, out var userId); 

            logger.LogInformation("Attempting to create quote for user {UserId}", userId);
            
            using var activity = ActivitySource.StartActivity("compute-quote-creation");
            activity?.SetTag("user.id", userId);

            var result = Quote.Create(request.Author, request.Text, clock.UtcNow, userId);
            if (!result.IsSuccess) 
            {
                logger.LogWarning("Failed to create quote for user {UserId}. Reason: {Reason}", userId, result.Error);
                return Results.BadRequest(result.Error);
            }
            
            await repo.AddAsync(result.Value!, ct);
            
            logger.LogInformation("Successfully created quote {QuoteId} for user {UserId}", result.Value!.Id, userId);
            
            return Results.Created($"/api/quotes/{result.Value!.Id}", result.Value);
        }).RequireAuthorization("can-edit-quotes");

        group.MapPut("/{id:int}/author", async (int id, UpdateAuthorRequest request, IQuoteRepository repo, CancellationToken ct) =>
        {
            var quote = await repo.GetByIdAsync(id, ct);
            if (quote is null) return Results.NotFound();

            var result = quote.ChangeAuthor(request.Author);
            if (!result.IsSuccess) return Results.BadRequest(result.Error);

            await repo.UpdateAsync(quote, ct);
            return Results.NoContent();
        }).RequireAuthorization("can-edit-quotes");

        group.MapDelete("/{id:int}", async (int id, IQuoteRepository repo, IAuthorizationService authService, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var quote = await repo.GetByIdAsync(id, ct);
            if (quote is null) return Results.NotFound();

            var authResult = await authService.AuthorizeAsync(user, id, "IsQuoteOwner");
            if (!authResult.Succeeded)
            {
                return Results.Forbid(); 
            }

            quote.Delete();
            await repo.UpdateAsync(quote, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // --- DAY 11 PIECE 2: OPTIMIZED ENDPOINT ---
        // Eliminating N+1 using a DTO projection (Select)
        group.MapGet("/fast", async (AppDbContext db, CancellationToken ct) =>
        {
            var userQuoteData = await db.Users
                .AsNoTracking()
                .Select(u => new 
                {
                    UserId = u.Id,
                    QuoteCount = db.Quotes.Count(q => q.UserId == u.Id && !q.IsDeleted)
                })
                .ToListAsync(ct);
            
            return Results.Ok(userQuoteData);
        });
    }
}