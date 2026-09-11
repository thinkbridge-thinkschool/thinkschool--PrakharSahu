using Microsoft.AspNetCore.Authorization;
using QuotesApi.Repositories;
using System.Security.Claims;

namespace QuotesApi.Authorization;

public class IsOwnerHandler : AuthorizationHandler<IsOwnerRequirement, int>
{
    private readonly IQuoteRepository _quoteRepository;

    public IsOwnerHandler(IQuoteRepository quoteRepository)
    {
        _quoteRepository = quoteRepository;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, 
        IsOwnerRequirement requirement, 
        int quoteId)
    {
        // Try to get the user's ID from the token claims
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                          ?? context.User.FindFirst("sub")?.Value;

        if (userIdClaim == null || !int.TryParse(userIdClaim, out var userId))
        {
            context.Fail();
            return;
        }

        // Fetch the quote from the database
        var quote = await _quoteRepository.GetByIdAsync(quoteId);
        if (quote == null)
        {
            context.Fail();
            return;
        }

        // Check if the user trying to delete it is the one who created it
        if (quote.UserId == userId)
        {
            context.Succeed(requirement); // Authorized!
        }
        else
        {
            context.Fail(); // Denied!
        }
    }
}
