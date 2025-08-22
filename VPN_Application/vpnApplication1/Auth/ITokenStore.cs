// Auth/ITokenStore.cs
using System.Text.Json;

public interface ITokenStore
{
    Task<AuthTokens?> LoadAsync();
    Task SaveAsync(AuthTokens tokens);
    Task ClearAsync();
}

