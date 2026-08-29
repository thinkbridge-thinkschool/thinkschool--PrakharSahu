using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using QuotesApi.Models;
using QuotesApi.Dtos;
using QuotesApi.Repositories;
using QuotesApi.Services;

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
            // System.Threading.Thread.Sleep(1500);

            var result = Quote.Create(request.Author, request.Text, clock.UtcNow, userId);
            if (!result.IsSuccess) 
            {
                logger.LogWarning("Failed to create quote for user {UserId}. Reason: {Reason}", userId, result.Error);
                return Results.BadRequest(result.Error);
            }
            
            // Same author, same words = a double post. A different author saying the same thing
            // is allowed through, so this is scoped to the author rather than to the text alone.
            if (await repo.ExistsForAuthorAsync(request.Author, request.Text, null, ct))
            {
                logger.LogWarning("Rejected duplicate quote from user {UserId}", userId);
                return Results.Conflict(new DomainError(
                    "This author has already posted that quote. The same words credited to a different author are fine."));
            }

            await repo.AddAsync(result.Value!, ct);

            logger.LogInformation("Successfully created quote {QuoteId} for user {UserId}", result.Value!.Id, userId);
            
            return Results.Created($"/api/quotes/{result.Value!.Id}", result.Value);
        }).RequireAuthorization("can-edit-quotes");

        group.MapPut("/{id:int}/author", async (int id, UpdateAuthorRequest request, IQuoteRepository repo, CancellationToken ct) =>
        {
            var quote = await repo.GetByIdAsync(id, ct);
            if (quote is null) return Results.NotFound();

            // Re-crediting a quote can walk it into a duplicate: if the new author already has
            // these exact words, saving would give them the same quote twice. `id` is excluded
            // so the row never collides with itself.
            if (await repo.ExistsForAuthorAsync(request.Author, quote.Text, id, ct))
            {
                return Results.Conflict(new DomainError(
                    "That author already has this quote. Credit it to someone else, or delete the existing one."));
            }

            // ChangeAuthor mutates the tracked entity, but nothing is persisted until
            // UpdateAsync runs — an early return here leaves the database untouched.
            var result = quote.ChangeAuthor(request.Author);
            if (!result.IsSuccess) return Results.BadRequest(result.Error);

            await repo.UpdateAsync(quote, ct);
            return Results.NoContent();
        }).RequireAuthorization("can-edit-quotes");

        // DELETE applies the Custom Authorization Requirement Handler
        group.MapDelete("/{id:int}", async (int id, IQuoteRepository repo, IAuthorizationService authService, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var quote = await repo.GetByIdAsync(id, ct);
            if (quote is null) return Results.NotFound();

            var authResult = await authService.AuthorizeAsync(user, id, "IsQuoteOwner");
            if (!authResult.Succeeded)
            {
                return Results.Forbid(); // 403 if they don't own it
            }

            quote.Delete();
            await repo.UpdateAsync(quote, ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}








