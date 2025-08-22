// Auth/IAuthService.cs
public interface IAuthService
{
    bool IsLoggedIn { get; }
    AuthTokens? CurrentTokens { get; }
    Task InitializeAsync(); // один раз при старте
    Task LoginAsync(string email, string password);
    Task LogoutAsync();
    Task RegisterAsync(string email, string password);
    Task<string?> EnsureValidAccessTokenAsync(); // вернёт живой access (обновит при необходимости)
}
