using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace Dispatch.Api.Security;

/// <summary>
/// Authentication and authorization for Dispatch.
/// </summary>
/// <remarks>
/// <para>
/// Day 31 left this out and said why: <i>"Dispatch has no notion of a caller, and adding a scheme
/// before the roles exist is security theatre."</i> That was right. Bolting a bearer check onto
/// every endpoint would have produced a system where everyone who can authenticate can do
/// everything, which is authentication without authorization and proves nothing about who may
/// raise an invoice.
/// </para>
/// <para>
/// The roles now exist, and they come from the domain rather than from a permissions vocabulary:
/// the state machine already distinguishes the person who <i>books</i> work from the person who
/// <i>does</i> it. Those are the two roles, and no others were invented.
/// </para>
/// <list type="table">
///   <item>
///     <term>Dispatch.Dispatcher</term>
///     <description>raise, triage, schedule, cancel — decides that work should happen</description>
///   </item>
///   <item>
///     <term>Dispatch.Technician</term>
///     <description>start, log labour, complete — reports that work did happen</description>
///   </item>
///   <item>
///     <term>Dispatch.Read</term>
///     <description>read a work order or the invoice list</description>
///   </item>
/// </list>
/// <para>
/// <b>Why a technician cannot schedule.</b> The separation is not bureaucratic: labour hours become
/// an invoice, so the person who records the hours and the person who commits the customer to the
/// visit should not be the same principal. That is the same reasoning behind not letting the
/// application write its own JWT signing key.
/// </para>
/// <para>
/// <b>Why this is optional.</b> Absent configuration disables the whole scheme, exactly as messaging
/// and caller-identity do elsewhere in this repository. Twenty-six integration tests and one
/// end-to-end test boot this application; making authentication mandatory would mean every one of
/// them had to mint a token, which is a lot of machinery to prove something they are not testing.
/// The deployed app always configures it, and <c>verify.sh</c> asserts that it did.
/// </para>
/// </remarks>
public static class DispatchAuth
{
    public const string DispatcherPolicy = "dispatcher";
    public const string TechnicianPolicy = "technician";
    public const string ReadPolicy = "read";

    private const string DispatcherRole = "Dispatch.Dispatcher";
    private const string TechnicianRole = "Dispatch.Technician";
    private const string ReadRole = "Dispatch.Read";

    /// <summary>True when a tenant and an audience have both been configured.</summary>
    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Auth:TenantId"])
        && !string.IsNullOrWhiteSpace(configuration["Auth:Audience"]);

    public static IServiceCollection AddDispatchAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        ILogger? bootstrapLogger = null)
    {
        if (!IsConfigured(configuration))
        {
            bootstrapLogger?.LogWarning(
                "Authentication is DISABLED: Auth:TenantId and Auth:Audience are not both set. "
                + "Every endpoint is open. This is the local and test configuration, never the "
                + "deployed one.");

            // The policies still have to exist, because the endpoints reference them by name and
            // a missing policy is an InvalidOperationException at the first request rather than a
            // clean "no authentication configured". Open policies, named the same, so exactly one
            // thing differs between the two modes.
            services.AddAuthorizationBuilder()
                .AddPolicy(DispatcherPolicy, p => p.RequireAssertion(_ => true))
                .AddPolicy(TechnicianPolicy, p => p.RequireAssertion(_ => true))
                .AddPolicy(ReadPolicy, p => p.RequireAssertion(_ => true));

            return services;
        }

        var tenantId = configuration["Auth:TenantId"]!;
        var audience = configuration["Auth:Audience"]!;

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // v2.0, not v1.0. The two endpoints issue tokens with different claim shapes and
                // different issuer values; validation against the wrong one fails with
                // "IDX10205: Issuer validation failed", which reads like a misconfigured tenant.
                options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
                options.Audience = audience;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers =
                    [
                        $"https://login.microsoftonline.com/{tenantId}/v2.0",
                        $"https://sts.windows.net/{tenantId}/"
                    ],
                    ValidateAudience = true,
                    ValidAudiences = [audience, audience.Replace("api://", string.Empty)],
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,

                    // Five minutes is the default and is too generous for a token this powerful.
                    // Thirty seconds absorbs ordinary clock drift and nothing else.
                    ClockSkew = TimeSpan.FromSeconds(30),

                    // Entra puts app roles in "roles" for both user and application tokens. Saying
                    // so explicitly means RequireRole works without a claim-mapping surprise.
                    RoleClaimType = "roles"
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(DispatcherPolicy, p => p.RequireRole(DispatcherRole))
            .AddPolicy(TechnicianPolicy, p => p.RequireRole(TechnicianRole))

            // Read is satisfied by ANY of the three. A dispatcher who cannot read the order they
            // just raised would be a rule that exists only to be worked around.
            .AddPolicy(ReadPolicy, p => p.RequireRole(ReadRole, DispatcherRole, TechnicianRole));

        bootstrapLogger?.LogInformation(
            "Authentication ENABLED: tenant {Tenant}, audience {Audience}.", tenantId, audience);

        return services;
    }
}
