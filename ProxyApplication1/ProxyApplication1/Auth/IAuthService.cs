// Auth/IAuthService.cs
public interface IAuthService
{
    bool IsLoggedIn { get; }
    AuthTokens? CurrentTokens { get; }
    Task InitializeAsync(); // один раз при старте
    Task LoginAsync(string email, string password, bool rememberMe);
    Task LogoutAsync();
    Task RegisterAsync(string email, string password, bool rememberMe);
    Task<string?> EnsureValidAccessTokenAsync(); // вернёт живой access (обновит при необходимости)
}
