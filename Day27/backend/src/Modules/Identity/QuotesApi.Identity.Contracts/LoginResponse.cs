namespace QuotesApi.Identity.Contracts;
public record LoginResponse(string AccessToken, string RefreshToken, int ExpiresIn);
