using Microsoft.AspNetCore.Authorization;

namespace QuotesApi.Quotes.Infrastructure;

public class IsOwnerRequirement : IAuthorizationRequirement { }
