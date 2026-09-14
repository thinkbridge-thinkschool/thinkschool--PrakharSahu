using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Identity.Application;

namespace QuotesApi.Identity.Infrastructure;

/// <summary>
/// Who the caller is. Authentication schemes, the token options, and the endpoints that issue
/// and redeem credentials.
/// </summary>
/// <remarks>
/// This is the module that most obviously wanted to exist. Authentication was previously wired
/// inside <c>AddInfrastructure</c> next to the quote repository, so the two most security-
/// sensitive decisions in the application — which signing key is acceptable, and which issuer
/// is trusted — sat in a method whose name suggested it was about databases.
/// </remarks>
public static class IdentityModule
{
    private const string PublicCloudInstance = "https://login.microsoftonline.com/";

    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.Configure<JwtOptions>(config.GetSection("Jwt"));

        // The signing key is deliberately not defaulted. A fallback key checked into source
        // means anyone who can read the repo can mint valid tokens, so refuse to start rather
        // than sign with a publicly known secret.
        var signingKey = config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException(
                "Jwt:Key is not configured. Set the Jwt__Key environment variable " +
                "(locally in .env, in Azure via 'azd env set JWT_SIGNING_KEY <value>').");
        }

        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException(
                "Jwt:Key must be at least 32 bytes to sign with HMAC-SHA256.");
        }

        // Entra is optional. Previously the Entra scheme was always registered and built its
        // authority from EntraId:Instance, which was never set -- producing a schemeless
        // authority that throws the first time a Microsoft-issued token arrives.
        var entraTenantId = config["EntraId:TenantId"];
        var entraAudience = config["EntraId:Audience"];
        var entraEnabled = !string.IsNullOrWhiteSpace(entraTenantId)
                           && !string.IsNullOrWhiteSpace(entraAudience);

        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = "Dynamic";
            options.DefaultChallengeScheme = "Dynamic";
        })
        .AddJwtBearer("SelfHosted", options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = config["Jwt:Issuer"],
                ValidAudience = config["Jwt:Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                ClockSkew = TimeSpan.Zero
            };
        });

        if (entraEnabled)
        {
            var instance = config["EntraId:Instance"];
            if (string.IsNullOrWhiteSpace(instance))
            {
                instance = PublicCloudInstance;
            }
            if (!instance.EndsWith('/'))
            {
                instance += "/";
            }

            authentication.AddJwtBearer("Entra", options =>
            {
                options.Authority = $"{instance}{entraTenantId}/v2.0";
                options.Audience = entraAudience;
            });
        }

        authentication.AddPolicyScheme("Dynamic", "JWT or Entra", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                if (!entraEnabled)
                {
                    return "SelfHosted";
                }

                var authHeader = context.Request.Headers.Authorization.ToString();
                if (authHeader.StartsWith("Bearer "))
                {
                    var token = authHeader.Substring("Bearer ".Length).Trim();
                    var handler = new JwtSecurityTokenHandler();
                    if (handler.CanReadToken(token))
                    {
                        var jwt = handler.ReadJwtToken(token);
                        if (jwt.Issuer.Contains("login.microsoftonline.com") || jwt.Issuer.Contains("sts.windows.net"))
                        {
                            return "Entra";
                        }
                    }
                }
                return "SelfHosted";
            };
        });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("can-edit-quotes", policy =>
                policy.RequireClaim("scope", "quotes.write"));
        });

        return services;
    }

    public static IEndpointRouteBuilder MapIdentityModule(this IEndpointRouteBuilder app)
    {
        app.MapAuthEndpoints();
        app.MapWhoAmI();
        return app;
    }
}
