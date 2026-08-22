using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Authorization;
using QuotesApi.Data;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

public static class InfrastructureExtensions
{
    private const string PublicCloudInstance = "https://login.microsoftonline.com/";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(config.GetConnectionString("DefaultConnection") ?? "Data Source=quotes.db"));

        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddScoped<IAuthorizationHandler, IsOwnerHandler>();

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

            options.AddPolicy("IsQuoteOwner", policy =>
                policy.Requirements.Add(new IsOwnerRequirement()));
        });

        return services;
    }
}
