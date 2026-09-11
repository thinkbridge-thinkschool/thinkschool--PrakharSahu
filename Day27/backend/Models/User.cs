namespace QuotesApi.Models;

public class User
{
    public int Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string PasswordHash { get; init; } = string.Empty;
}
