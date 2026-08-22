namespace QuotesApi.Dtos;
public record LoginResponse(string AccessToken, string RefreshToken, int ExpiresIn);
