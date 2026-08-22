using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim(JwtRegisteredClaimNames.Email, user.Email) };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(options.Issuer, options.Audience, claims, expires: DateTime.UtcNow.AddSeconds(options.ExpiresInSeconds), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", async ([FromBody] LoginRequest request, AppDbContext db, IOptions<JwtOptions> jwtOptions, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email, ct);
            if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                return Results.Unauthorized();

            var accessToken = GenerateJwt(user, jwtOptions.Value);
            var (refreshToken, tokenHash) = GenerateRefreshToken();
            
            var familyId = Guid.NewGuid().ToString();
            db.RefreshTokens.Add(new RefreshToken { TokenHash = tokenHash, UserId = user.Id, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), FamilyId = familyId });
            await db.SaveChangesAsync(ct);

            return Results.Ok(new LoginResponse(accessToken, refreshToken, jwtOptions.Value.ExpiresInSeconds));
        });

        group.MapPost("/refresh", async ([FromBody] RefreshRequest request, AppDbContext db, IOptions<JwtOptions> jwtOptions, ILogger<Program> logger, CancellationToken ct) =>
        {
            var hash = HashToken(request.RefreshToken);
            var storedToken = await db.RefreshTokens.FirstOrDefaultAsync(rt => rt.TokenHash == hash, ct);

            if (storedToken == null) return Results.Unauthorized();

            if (!storedToken.IsActive)
            {
                logger.LogWarning("SECURITY ALERT: Attempted reuse of revoked refresh token. Revoking token family {FamilyId}.", storedToken.FamilyId);
                var familyTokens = await db.RefreshTokens.Where(rt => rt.FamilyId == storedToken.FamilyId).ToListAsync(ct);
                foreach (var token in familyTokens) { token.RevokedAt = DateTimeOffset.UtcNow; }
                await db.SaveChangesAsync(ct);
                return Results.Unauthorized();
            }

            var user = await db.Users.FindAsync(new object[] { storedToken.UserId }, ct);
            if (user == null) return Results.Unauthorized();

            var newAccessToken = GenerateJwt(user, jwtOptions.Value);
            var (newRefreshToken, newHash) = GenerateRefreshToken();

            storedToken.RevokedAt = DateTimeOffset.UtcNow;
            storedToken.ReplacedByToken = newHash;

            db.RefreshTokens.Add(new RefreshToken { TokenHash = newHash, UserId = user.Id, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), FamilyId = storedToken.FamilyId });
            await db.SaveChangesAsync(ct);

            return Results.Ok(new LoginResponse(newAccessToken, newRefreshToken, jwtOptions.Value.ExpiresInSeconds));
        });

        group.MapPost("/logout", async ([FromBody] RefreshRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var hash = HashToken(request.RefreshToken);
            var storedToken = await db.RefreshTokens.FirstOrDefaultAsync(rt => rt.TokenHash == hash, ct);
            
            if (storedToken != null && storedToken.IsActive)
            {
                storedToken.RevokedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
