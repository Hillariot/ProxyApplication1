// Auth/SecureTokenStore.cs
using System.Text.Json;

public sealed class SecureTokenStore : ITokenStore
{
    private const string Key = "auth_tokens_v1";

    public async Task<AuthTokens?> LoadAsync()
    {
        try
        {
            var json = await SecureStorage.GetAsync(Key);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AuthTokens>(json);
        }
        catch { return null; }
    }

    public async Task SaveAsync(AuthTokens tokens)
    {
        var json = JsonSerializer.Serialize(tokens);
        await SecureStorage.SetAsync(Key, json);
    }

    public Task ClearAsync()
    {
        SecureStorage.Remove(Key);
        return Task.CompletedTask;
    }
}
