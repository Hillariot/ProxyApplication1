using System.Net.Http;
using System.Net.Http.Json;

public sealed class AuthService : IAuthService
{
    private readonly ITokenStore _store;
    private readonly HttpClient _http; // сырой клиент ТОЛЬКО для auth-эндпоинтов
    private readonly CustomAuthStateProvider _stateProvider;
    private readonly TimeSpan _skew = TimeSpan.FromSeconds(60);

    public AuthTokens? CurrentTokens { get; private set; }
    public object? CurrentConfig { get; private set; } // для хранения config
    public bool IsLoggedIn => CurrentTokens is not null;

    /// <summary>Показывает, сохранены ли токены в SecureStorage.</summary>
    public bool IsPersisted { get; private set; }

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
        IsPersisted = CurrentTokens is not null;

        // при необходимости можно подгрузить config из хранилища
        await _stateProvider.UpdatePrincipalAsync(CurrentTokens?.AccessToken);
    }

    // ===== Публичные API: с rememberMe =====

    public async Task LoginAsync(string email, string password, bool rememberMe)
    {
        var requestData = new
        {
            email,
            password,
            platform = "WINDOWS",
            encrypted = false,
        };

        var resp = await _http.PostAsJsonAsync("auth_auth", requestData);
        resp.EnsureSuccessStatusCode();

        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>()
                  ?? throw new InvalidOperationException("Empty login response");

        CurrentTokens = new AuthTokens
        {
            AccessToken = dto.AccessToken,
            RefreshToken = dto.RefreshToken,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(dto.ExpiresInSeconds)
        };
        CurrentConfig = dto.Config;

        if (rememberMe)
        {
            await _store.SaveAsync(CurrentTokens);
            IsPersisted = true;
        }
        else
        {
            await _store.ClearAsync(); // не оставляем старые токены
            IsPersisted = false;
        }

        await _stateProvider.UpdatePrincipalAsync(CurrentTokens.AccessToken);
    }

    public async Task RegisterAsync(string email, string password, bool rememberMe)
    {
        var resp = await _http.PostAsJsonAsync("auth_register", new { email, password });
        resp.EnsureSuccessStatusCode();

        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>()
                  ?? throw new InvalidOperationException("Empty register response");

        CurrentTokens = new AuthTokens
        {
            AccessToken = dto.AccessToken,
            RefreshToken = dto.RefreshToken,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(dto.ExpiresInSeconds)
        };
        CurrentConfig = dto.Config;

        if (rememberMe)
        {
            await _store.SaveAsync(CurrentTokens);
            IsPersisted = true;
        }
        else
        {
            await _store.ClearAsync();
            IsPersisted = false;
        }

        await _stateProvider.UpdatePrincipalAsync(CurrentTokens.AccessToken);
    }

    // ===== Сохранить текущую сессию позже (кнопкой "Сохранить авторизацию") =====
    public async Task SaveCurrentToStorageAsync()
    {
        if (CurrentTokens is null) throw new InvalidOperationException("Нет активной сессии.");
        await _store.SaveAsync(CurrentTokens);
        IsPersisted = true;
    }

    // ===== Совместимость со старым кодом интерфейса =====

    public Task LoginAsync(string email, string password)
        => LoginAsync(email, password, rememberMe: true);

    public Task RegisterAsync(string email, string password)
        => RegisterAsync(email, password, rememberMe: true);

    // ===== Logout / Refresh =====

    public async Task LogoutAsync()
    {
        CurrentTokens = null;
        CurrentConfig = null;
        IsPersisted = false;
        await _store.ClearAsync();
        await _stateProvider.UpdatePrincipalAsync(null);
    }

    public async Task<string?> EnsureValidAccessTokenAsync()
    {
        if (CurrentTokens is null) return null;

        var now = DateTimeOffset.UtcNow;
        if (CurrentTokens.ExpiresAtUtc - now > _skew)
            return CurrentTokens.AccessToken;

        // refresh flow (без ведущего слэша — используем BaseAddress)
        var resp = await _http.PostAsJsonAsync("auth_refresh", new { refreshToken = CurrentTokens.RefreshToken });
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
            RefreshToken = dto.RefreshToken ?? CurrentTokens.RefreshToken,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(dto.ExpiresInSeconds)
        };

        if (dto.Config != null)
            CurrentConfig = dto.Config;

        if (IsPersisted) // сохраняем только если сессия помечена как персистентная
            await _store.SaveAsync(CurrentTokens);

        await _stateProvider.UpdatePrincipalAsync(CurrentTokens.AccessToken);
        return CurrentTokens.AccessToken;
    }

    private sealed record LoginResponseDto(
        string AccessToken,
        string? RefreshToken,
        int ExpiresInSeconds,
        object? Config = null
    );
}
