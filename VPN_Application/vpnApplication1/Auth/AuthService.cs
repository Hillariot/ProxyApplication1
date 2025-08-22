// Auth/AuthService.cs
using System.Net.Http;
using System.Net.Http.Json;


public sealed class AuthService : IAuthService
{
    private readonly ITokenStore _store;
    private readonly HttpClient _http; // сырой клиент ТОЛЬКО для auth-эндпоинтов
    private readonly CustomAuthStateProvider _stateProvider;
    private readonly TimeSpan _skew = TimeSpan.FromSeconds(60);

    public AuthTokens? CurrentTokens { get; private set; }
    public bool IsLoggedIn => CurrentTokens is not null;

    public AuthService(ITokenStore store,
                       IHttpClientFactory httpFactory,
                       CustomAuthStateProvider stateProvider)
    {
        _store = store;
        _http = httpFactory.CreateClient("Auth"); // без токено-подставлялок
        _stateProvider = stateProvider;
    }

    public async Task InitializeAsync()
    {
        CurrentTokens = await _store.LoadAsync();
        await _stateProvider.UpdatePrincipalAsync(CurrentTokens?.AccessToken);
    }

    public async Task LoginAsync(string email, string password)
    {
        var resp = await _http.PostAsJsonAsync("auth_auth", new { email, password }); // <-- сюда
        resp.EnsureSuccessStatusCode();

        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>()
                  ?? throw new InvalidOperationException("Empty login response");

        CurrentTokens = new AuthTokens
        {
            AccessToken = dto.AccessToken,
            RefreshToken = dto.RefreshToken,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(dto.ExpiresInSeconds)
        };

        await _store.SaveAsync(CurrentTokens);
        await _stateProvider.UpdatePrincipalAsync(CurrentTokens.AccessToken);
    }

    public async Task LogoutAsync()
    {
        CurrentTokens = null;
        await _store.ClearAsync();
        await _stateProvider.UpdatePrincipalAsync(null);
    }

    public async Task<string?> EnsureValidAccessTokenAsync()
    {
        if (CurrentTokens is null) return null;

        var now = DateTimeOffset.UtcNow;
        if (CurrentTokens.ExpiresAtUtc - now > _skew)
            return CurrentTokens.AccessToken;

        // refresh flow
        var resp = await _http.PostAsJsonAsync("/auth/refresh", new { refreshToken = CurrentTokens.RefreshToken });
        if (!resp.IsSuccessStatusCode)
        {
            await LogoutAsync();
            return null;
        }

        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>()
                  ?? throw new InvalidOperationException("Empty refresh response");

        CurrentTokens = new AuthTokens
        {
            AccessToken = dto.AccessToken,
            RefreshToken = dto.RefreshToken ?? CurrentTokens.RefreshToken, // на случай, если бэкенд не меняет
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(dto.ExpiresInSeconds)
        };

        await _store.SaveAsync(CurrentTokens);
        await _stateProvider.UpdatePrincipalAsync(CurrentTokens.AccessToken);

        return CurrentTokens.AccessToken;
    }

    public async Task RegisterAsync(string email, string password)
    {
        var resp = await _http.PostAsJsonAsync("/auth/register", new { email, password });
        resp.EnsureSuccessStatusCode();

        // Многие бэки сразу возвращают токены как при логине:
        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>()
                  ?? throw new InvalidOperationException("Empty register response");

        CurrentTokens = new AuthTokens
        {
            AccessToken = dto.AccessToken,
            RefreshToken = dto.RefreshToken,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(dto.ExpiresInSeconds)
        };
        await _store.SaveAsync(CurrentTokens);
        await _stateProvider.UpdatePrincipalAsync(CurrentTokens.AccessToken);
    }


    private sealed record LoginResponseDto(string AccessToken, string? RefreshToken, int ExpiresInSeconds);
}
