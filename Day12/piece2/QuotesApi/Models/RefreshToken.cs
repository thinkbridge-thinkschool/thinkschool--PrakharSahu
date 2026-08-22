using System;

namespace QuotesApi.Models;

public class RefreshToken
{
    public int Id { get; init; }
    public string TokenHash { get; init; } = string.Empty;
    public int UserId { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByToken { get; set; }
    public string FamilyId { get; init; } = string.Empty; 

    public bool IsActive => RevokedAt == null && DateTimeOffset.UtcNow < ExpiresAt;
}
