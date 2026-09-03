using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using QuotesApi.Extensions;

namespace QuotesApi.Endpoints;

/// <summary>
/// <c>GET /api/whoami</c> — reports the two identities the API just validated.
/// </summary>
/// <remarks>
/// <para>
/// This exists to make the managed-identity hop observable. Without it, "the call carries a
/// managed-identity token" is a claim about code; with it, the API itself says which service
/// principal it authenticated and which app roles that principal presented, so the
/// verification log quotes the server rather than the client.
/// </para>
/// <para>
/// Everything returned is an identifier, never a credential: object ids, application ids and
/// role names mean nothing to anyone who cannot already authenticate as that principal, and
/// the same values are sitting in this repository's deploy scripts. The tokens themselves are
/// never echoed.
/// </para>
/// <para>
/// It sits behind the same caller-token requirement as the rest of <c>/api/*</c>, so an
/// anonymous request gets the same 401 as any other endpoint — which is itself the negative
/// half of the proof.
/// </para>
/// </remarks>
public static class WhoAmIEndpoint
{
    /// <summary>
    /// Reports whether the caller is an application or a user.
    /// </summary>
    /// <remarks>
    /// Entra stamps <c>idtyp</c> only on v2.0 tokens, and a managed identity asking IMDS for a
    /// token against a custom API gets a v1.0 one back — issuer <c>sts.windows.net</c>, no
    /// <c>idtyp</c> anywhere. Reporting "(not stamped)" was accurate and useless: it made a
    /// correctly-authenticated managed identity look like something the API could not
    /// identify.
    ///
    /// Falling back to inference is sound because the distinction is visible in the shape of
    /// the token. A delegated token always carries something naming the human — a scope, a
    /// UPN, a display name. An app-only token never does. So an <c>appid</c> with none of
    /// those present is an application, and saying so is a statement about the token rather
    /// than a guess.
    /// </remarks>
    private static string DescribeIdentityType(ClaimsPrincipal caller)
    {
        var stamped = caller.FindFirst("idtyp")?.Value;
        if (!string.IsNullOrWhiteSpace(stamped))
        {
            return stamped;
        }

        var hasUserClaim =
            caller.FindFirst("scp") is not null ||
            caller.FindFirst("upn") is not null ||
            caller.FindFirst("preferred_username") is not null ||
            caller.FindFirst("name") is not null;

        var hasApplicationId =
            caller.FindFirst("appid") is not null || caller.FindFirst("azp") is not null;

        return (hasApplicationId, hasUserClaim) switch
        {
            (true, false) => "app",
            (_, true) => "user",
            _ => "unknown"
        };
    }

    public static void MapWhoAmI(this WebApplication app)
    {
        app.MapGet("/api/whoami", (HttpContext context) =>
        {
            var caller = context.Items[CallerIdentityExtensions.SchemeName] as ClaimsPrincipal;
            var user = context.User;

            return Results.Ok(new
            {
                callerIdentity = caller is null
                    ? null
                    : new
                    {
                        // Present only on app-only tokens. Its presence, together with the
                        // absence of a user subject below, is what distinguishes a managed
                        // identity from a signed-in human.
                        applicationId = caller.FindFirst("appid")?.Value
                                        ?? caller.FindFirst("azp")?.Value,
                        objectId = caller.FindFirst("oid")?.Value,
                        tenantId = caller.FindFirst("tid")?.Value,
                        audience = caller.FindFirst("aud")?.Value,
                        issuer = caller.FindFirst("iss")?.Value,
                        roles = caller.FindAll("roles")
                                      .Concat(caller.FindAll(ClaimTypes.Role))
                                      .Select(c => c.Value)
                                      .Distinct()
                                      .ToArray(),
                        identityType = DescribeIdentityType(caller),
                        // A delegated token would carry one of these. An app-only token
                        // never does, so null here is the evidence no user was involved.
                        userPrincipalName = caller.FindFirst("preferred_username")?.Value
                                            ?? caller.FindFirst("upn")?.Value
                    },
                endUser = user?.Identity?.IsAuthenticated == true
                    ? new
                    {
                        subject = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                                  ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                        email = user.FindFirst(JwtRegisteredClaimNames.Email)?.Value
                                ?? user.FindFirst(ClaimTypes.Email)?.Value,
                        scopes = user.FindAll("scope").Select(c => c.Value).ToArray(),
                        // Which JwtBearer scheme accepted them — "SelfHosted" for a token
                        // this API minted, "Entra" for a Microsoft-issued one.
                        authenticationType = user.Identity.AuthenticationType
                    }
                    : null,
                // Anonymous reads are still anonymous: GET /api/quotes needs no user. Saying
                // so explicitly stops the null above from reading like a bug.
                note = "endUser is null for anonymous requests; callerIdentity is never null when "
                       + "CallerIdentity enforcement is enabled."
            });
        });
    }
}
