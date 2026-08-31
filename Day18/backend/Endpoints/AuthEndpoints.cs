using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Options;

namespace QuotesApi.Endpoints;

public static class AuthEndpoints
{
    private static string HashToken(string token)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }

    private static (string Token, string Hash) GenerateRefreshToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        return (token, HashToken(token));
    }

    private static string GenerateJwt(User user, JwtOptions options)
    {
        // The "can-edit-quotes" policy requires a scope claim (InfrastructureExtensions.cs:141).
        // Without it every token this API issues is rejected by its own write endpoints with a 403,
        // so POST /api/quotes and PUT /api/quotes/{id}/author were unreachable for any signed-in user.
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("scope", "quotes.write")
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(options.Issuer, options.Audience, claims, expires: DateTime.UtcNow.AddSeconds(options.ExpiresInSeconds), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Name of the cookie carrying the refresh token.</summary>
    public const string RefreshCookieName = "quotes_rt";


    private static void AppendRefreshCookie(HttpResponse response, string refreshToken)
    {
        response.Cookies.Append(RefreshCookieName, refreshToken, new CookieOptions
        {
            HttpOnly = true,
            // Browsers treat http://localhost as a secure context, so this still works in
            // development without the https profile.
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            Expires = DateTimeOffset.UtcNow.Add(RefreshLifetime),
            IsEssential = true
        });
    }

    private static void DeleteRefreshCookie(HttpResponse response)
    {
        response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth"
        });
    }

    /// <summary>Cookie first; the body is still accepted so existing callers keep working.</summary>
    private static string? ReadRefreshToken(HttpRequest request, RefreshRequest? body)
    {
        var fromCookie = request.Cookies[RefreshCookieName];
        if (!string.IsNullOrWhiteSpace(fromCookie)) return fromCookie;
        return string.IsNullOrWhiteSpace(body?.RefreshToken) ? null : body!.RefreshToken;
    }

    /// <summary>How long a refresh token — and its cookie — stays valid.</summary>
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(7);

    private static LoginResponse StartSession(User user, AppDbContext db, HttpResponse response, JwtOptions options)
    {
        var accessToken = GenerateJwt(user, options);
        var (refreshToken, tokenHash) = GenerateRefreshToken();

        db.RefreshTokens.Add(new RefreshToken
        {
            TokenHash = tokenHash,
            UserId = user.Id,
            ExpiresAt = DateTimeOffset.UtcNow.Add(RefreshLifetime),
            FamilyId = Guid.NewGuid().ToString()
        });

        AppendRefreshCookie(response, refreshToken);

        return new LoginResponse(accessToken, string.Empty, options.ExpiresInSeconds);
    }

    /// <summary>Minimum password length accepted at registration.</summary>
    private const int MinimumPasswordLength = 8;


    private static string? DescribeInvalidRegistration(RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return "Email is required.";
        }

        if (!MailAddress.TryCreate(request.Email.Trim(), out _))
        {
            return "That does not look like an email address.";
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            return "Password is required.";
        }

        if (request.Password.Length < MinimumPasswordLength)
        {
            return $"Password must be at least {MinimumPasswordLength} characters.";
        }

        return null;
    }

    private static string NormaliseEmail(string email) => email.Trim().ToLowerInvariant();

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/register", async ([FromBody] RegisterRequest request, HttpResponse response, AppDbContext db, IOptions<JwtOptions> jwtOptions, ILogger<Program> logger, CancellationToken ct) =>
        {
            var problem = DescribeInvalidRegistration(request);
            if (problem is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["registration"] = new[] { problem }
                });
            }

            var email = NormaliseEmail(request.Email);

            // Checked here rather than relying solely on the unique index, because the index
            // only exists on databases created after it was added — the long-lived dev
            // quotes.db already contains duplicate emails from before this endpoint existed.
            if (await db.Users.AnyAsync(u => u.Email == email, ct))
            {
                return Results.Conflict(new { message = "An account with that email already exists." });
            }

            var user = new User
            {
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password)
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            // Saved twice on purpose: the refresh-token row needs the identity value SQLite
            // assigns to the user, which is only known after the first save.
            var session = StartSession(user, db, response, jwtOptions.Value);
            await db.SaveChangesAsync(ct);

            // Email only. Never the password, and never the hash.
            logger.LogInformation("Registered new user {UserId}.", user.Id);

            return Results.Created($"/api/users/{user.Id}", session);
        });

        group.MapPost("/login", async ([FromBody] LoginRequest request, HttpResponse response, AppDbContext db, IOptions<JwtOptions> jwtOptions, CancellationToken ct) =>
        {
            var email = NormaliseEmail(request.Email ?? string.Empty);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email || u.Email == request.Email, ct);
            if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                return Results.Unauthorized();

            var session = StartSession(user, db, response, jwtOptions.Value);
            await db.SaveChangesAsync(ct);

            return Results.Ok(session);
        });

        group.MapPost("/refresh", async (HttpRequest httpRequest, HttpResponse response, [FromBody] RefreshRequest? request, AppDbContext db, IOptions<JwtOptions> jwtOptions, ILogger<Program> logger, CancellationToken ct) =>
        {
            var presented = ReadRefreshToken(httpRequest, request);
            if (presented is null) return Results.Unauthorized();

            var hash = HashToken(presented);
            var storedToken = await db.RefreshTokens.FirstOrDefaultAsync(rt => rt.TokenHash == hash, ct);

            if (storedToken == null) return Results.Unauthorized();

            if (!storedToken.IsActive)
            {
                logger.LogWarning("SECURITY ALERT: Attempted reuse of revoked refresh token. Revoking token family {FamilyId}.", storedToken.FamilyId);
                var familyTokens = await db.RefreshTokens.Where(rt => rt.FamilyId == storedToken.FamilyId).ToListAsync(ct);
                foreach (var token in familyTokens) { token.RevokedAt = DateTimeOffset.UtcNow; }
                await db.SaveChangesAsync(ct);
                DeleteRefreshCookie(response);
                return Results.Unauthorized();
            }

            var user = await db.Users.FindAsync(new object[] { storedToken.UserId }, ct);
            if (user == null) return Results.Unauthorized();

            var newAccessToken = GenerateJwt(user, jwtOptions.Value);
            var (newRefreshToken, newHash) = GenerateRefreshToken();

            storedToken.RevokedAt = DateTimeOffset.UtcNow;
            storedToken.ReplacedByToken = newHash;

            db.RefreshTokens.Add(new RefreshToken { TokenHash = newHash, UserId = user.Id, ExpiresAt = DateTimeOffset.UtcNow.Add(RefreshLifetime), FamilyId = storedToken.FamilyId });
            await db.SaveChangesAsync(ct);

            // Rotation: the new token replaces the cookie, so the spent one is never resent.
            AppendRefreshCookie(response, newRefreshToken);

            return Results.Ok(new LoginResponse(newAccessToken, string.Empty, jwtOptions.Value.ExpiresInSeconds));
        });

        group.MapPost("/logout", async (HttpRequest httpRequest, HttpResponse response, [FromBody] RefreshRequest? request, AppDbContext db, CancellationToken ct) =>
        {
            var presented = ReadRefreshToken(httpRequest, request);
            if (presented is null)
            {
                DeleteRefreshCookie(response);
                return Results.NoContent();
            }

            var hash = HashToken(presented);
            var storedToken = await db.RefreshTokens.FirstOrDefaultAsync(rt => rt.TokenHash == hash, ct);
            
            if (storedToken != null && storedToken.IsActive)
            {
                storedToken.RevokedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            DeleteRefreshCookie(response);
            return Results.NoContent();
        });
    }
}
