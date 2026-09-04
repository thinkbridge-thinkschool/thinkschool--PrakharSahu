using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
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

    private const string DefaultDatabaseFile = "quotes.db";

    private static string ResolveConnectionString(IConfiguration config, IHostEnvironment environment)
    {
        var configured = config.GetConnectionString("DefaultConnection");
        var builder = new SqliteConnectionStringBuilder(
            string.IsNullOrWhiteSpace(configured)
                ? $"Data Source={DefaultDatabaseFile}"
                : configured);

        // ":memory:" and shared-cache in-memory names are not file paths; integration tests use
        // them and must be left exactly as written.
        if (!string.IsNullOrWhiteSpace(builder.DataSource)
            && !builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(
                Path.Combine(environment.ContentRootPath, builder.DataSource));
        }

        return builder.ToString();
    }

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration config,
        IHostEnvironment environment)
    {
        // Day 21: counts every SQL command EF sends, which is the "DB queries/sec" the exercise
        // asks to be measured. Registered here rather than with the cache because it belongs to
        // the DbContext, and because the counter must exist whether or not caching is wired up
        // — otherwise the "before" arm has nothing to count.
        services.AddSingleton<DbQueryCounter>();

        // The service-provider overload, so the interceptor can resolve the singleton counter
        // regardless of the order these extension methods are called in.
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
            options
                .UseSqlite(ResolveConnectionString(config, environment))
                .AddInterceptors(new DbQueryCounterInterceptor(
                    serviceProvider.GetRequiredService<DbQueryCounter>())));

        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddScoped<IAuthorizationHandler, IsOwnerHandler>();

        // The signing key is deliberately not defaulted. A fallback key checked
        // into source means anyone who can read the repo can mint valid tokens,
        // so refuse to start rather than sign with a publicly known secret.
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

        // Entra is optional. Previously the Entra scheme was always registered
        // and built its authority from EntraId:Instance, which was never set --
        // producing a schemeless authority that throws the first time a
        // Microsoft-issued token arrives.
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
