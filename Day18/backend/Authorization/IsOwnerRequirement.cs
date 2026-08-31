using Microsoft.AspNetCore.Authorization;

namespace QuotesApi.Authorization;

public class IsOwnerRequirement : IAuthorizationRequirement { }
