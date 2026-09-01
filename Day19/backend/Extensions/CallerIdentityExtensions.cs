using System.Security.Claims;
// HttpContext.AuthenticateAsync is an extension method living here, not a member of
// HttpContext. Without this using the middleware below fails to compile with CS1061, which
// reads like a missing package rather than a missing import.
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace QuotesApi.Extensions;

/// <summary>
/// Requires that every call to <c>/api/*</c> arrives with a valid Entra app-only token
/// proving it came through the trusted front door — the Static Web App's BFF — rather than
/// straight off the internet.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately separate from user authentication, and lives in its own header.
/// Two identities travel on one request:
/// </para>
/// <list type="bullet">
///   <item><c>Authorization</c> — the end user, holding a token this API issued itself.</item>
///   <item><c>X-Caller-Token</c> — the calling service, holding a token Entra issued to a
///   managed identity.</item>
/// </list>
/// <para>
/// Collapsing them into one header was the obvious first design and it is wrong: the
/// ownership rules in <see cref="Authorization.IsOwnerHandler"/> read <c>sub</c> off the
/// principal, so a managed-identity token in <c>Authorization</c> would make every quote
/// appear to belong to the proxy.
/// </para>
/// <para>
/// The whole scheme is off unless both <c>CallerIdentity:TenantId</c> and
/// <c>CallerIdentity:Audience</c> are configured, so Week-1's local runs and integration
/// tests behave exactly as they did before.
/// </para>
/// </remarks>
public static class CallerIdentityExtensions
{
    /// <summary>Header carrying the calling service's managed-identity token.</summary>
    public const string CallerTokenHeader = "X-Caller-Token";

    /// <summary>Authentication scheme name for that header.</summary>
    public const string SchemeName = "CallerIdentity";

    private const string PublicCloudInstance = "https://login.microsoftonline.com/";

    private static bool IsEnabled(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["CallerIdentity:TenantId"])
        && !string.IsNullOrWhiteSpace(config["CallerIdentity:Audience"]);

    public static IServiceCollection AddCallerIdentity(
        this IServiceCollection services,
        IConfiguration config)
    {
        if (!IsEnabled(config))
        {
            return services;
        }

        var tenantId = config["CallerIdentity:TenantId"]!;
        var audience = config["CallerIdentity:Audience"]!;

        services.AddAuthentication().AddJwtBearer(SchemeName, options =>
        {
            options.Authority = $"{PublicCloudInstance}{tenantId}/v2.0";
            options.Audience = audience;

            // Keep the claim names Entra actually sent.
            //
            // On by default, ASP.NET's inbound mapper rewrites short OIDC claim names into
            // long WS-Federation schema URIs — "oid" becomes
            // ".../identity/claims/objectidentifier", "roles" becomes
            // ".../identity/claims/role", and so on. Nothing fails; the claims are simply not
            // where anyone reading the token would look for them, so lookups by their real
            // names silently return null and an endpoint reporting the caller shows a
            // principal with no id and no roles while the token plainly has both.
            options.MapInboundClaims = false;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidAudiences = new[] { audience, StripApiPrefix(audience) },
                // v1.0 and v2.0 endpoints stamp different issuers for the same tenant, and
                // which one you get depends on the token version the app registration
                // requests — not on anything the caller controls. Accepting both avoids a
                // failure that only appears after someone edits the manifest.
                ValidIssuers = new[]
                {
                    $"{PublicCloudInstance}{tenantId}/v2.0",
                    $"https://sts.windows.net/{tenantId}/"
                },
                // Managed-identity tokens come back as v1.0 (issuer sts.windows.net), which
                // carries app roles in "roles". Stated explicitly so the role check does not
                // depend on the mapper being off.
                RoleClaimType = "roles",
                ClockSkew = TimeSpan.FromMinutes(2)
            };

            options.Events = new JwtBearerEvents
            {
                // The token is in our own header, not in Authorization — that one belongs to
                // the end user. Without this the scheme finds nothing and every call 401s.
                OnMessageReceived = context =>
                {
                    var header = context.Request.Headers[CallerTokenHeader].ToString();
                    if (!string.IsNullOrWhiteSpace(header))
                    {
                        context.Token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                            ? header["Bearer ".Length..].Trim()
                            : header.Trim();
                    }
                    return Task.CompletedTask;
                }
            };
        });

        return services;
    }

    /// <summary>
    /// Rejects <c>/api/*</c> requests that do not carry a valid caller token.
    /// </summary>
    /// <remarks>
    /// Scoped to <c>/api</c> so the Container Apps health probe on <c>/health</c> keeps
    /// working — a probe cannot hold a managed identity, and failing it would take the
    /// revision down rather than secure it.
    /// </remarks>
    public static WebApplication UseCallerIdentity(this WebApplication app)
    {
        if (!IsEnabled(app.Configuration))
        {
            app.Logger.LogWarning(
                "CallerIdentity is DISABLED: CallerIdentity:TenantId and CallerIdentity:Audience "
                + "are not both set, so /api/* accepts traffic from any origin. This is expected "
                + "locally and a misconfiguration in Azure.");
            return app;
        }

        var requiredRole = app.Configuration["CallerIdentity:RequiredRole"];

        app.Logger.LogInformation(
            "CallerIdentity is ENABLED for /api/*: audience={Audience} requiredRole={Role}",
            app.Configuration["CallerIdentity:Audience"],
            string.IsNullOrWhiteSpace(requiredRole) ? "(none)" : requiredRole);

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/api"),
            branch => branch.Use(async (context, next) =>
            {
                var result = await context.AuthenticateAsync(SchemeName);

                if (!result.Succeeded)
                {
                    await WriteRejection(
                        context,
                        StatusCodes.Status401Unauthorized,
                        "caller-token-invalid",
                        context.Request.Headers.ContainsKey(CallerTokenHeader)
                            ? "The caller token was present but did not validate."
                            : $"This API only accepts requests carrying a valid {CallerTokenHeader}.");
                    return;
                }

                // An app-only token has no user behind it. If one shows up here it means a
                // delegated token was sent where a service credential was expected, which is
                // a configuration mistake worth failing loudly rather than accepting.
                if (result.Principal?.FindFirst("idtyp")?.Value is string idType
                    && !string.Equals(idType, "app", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteRejection(
                        context,
                        StatusCodes.Status403Forbidden,
                        "caller-token-not-app-only",
                        "The caller token must be an application token, not a delegated user token.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(requiredRole)
                    && !result.Principal!.Claims.Any(c =>
                        c.Type is "roles" or ClaimTypes.Role && c.Value == requiredRole))
                {
                    await WriteRejection(
                        context,
                        StatusCodes.Status403Forbidden,
                        "caller-role-missing",
                        $"The caller token does not carry the '{requiredRole}' app role.");
                    return;
                }

                // Stashed so /api/whoami can report who the platform said this was, without
                // re-validating the token or touching the end user's principal.
                context.Items[SchemeName] = result.Principal;

                await next();
            }));

        return app;
    }

    /// <summary>App id URIs look like <c>api://&lt;guid&gt;</c>; some tokens carry the bare guid.</summary>
    private static string StripApiPrefix(string audience) =>
        audience.StartsWith("api://", StringComparison.OrdinalIgnoreCase)
            ? audience["api://".Length..]
            : audience;

    private static Task WriteRejection(HttpContext context, int status, string error, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { error, detail });
    }
}
