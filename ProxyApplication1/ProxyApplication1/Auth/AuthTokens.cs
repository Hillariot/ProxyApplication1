// Auth/AuthTokens.cs
public sealed class AuthTokens
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset ExpiresAtUtc { get; init; }  // когда access истекает
}
