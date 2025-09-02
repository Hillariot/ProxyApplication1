// Auth/CustomAuthStateProvider.cs
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
using System.Text.Json;

public sealed class CustomAuthStateProvider : AuthenticationStateProvider
{
    private ClaimsPrincipal _principal = new(new ClaimsIdentity());

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(new AuthenticationState(_principal));

    public Task UpdatePrincipalAsync(string? accessToken)
    {
        _principal = string.IsNullOrWhiteSpace(accessToken)
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity(ParseClaims(accessToken), "jwt"));

        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        return Task.CompletedTask;
    }

    private static IEnumerable<Claim> ParseClaims(string jwt)
    {
        // без валидации: чисто парс пэйлоада для UI
        var parts = jwt.Split('.');
        if (parts.Length < 2) yield break;
        var payload = parts[1].PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '=');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/')));

        using var doc = JsonDocument.Parse(json);
        foreach (var kv in doc.RootElement.EnumerateObject())
        {
            var value = kv.Value.ValueKind switch
            {
                JsonValueKind.Array => string.Join(",", kv.Value.EnumerateArray().Select(x => x.ToString())),
                _ => kv.Value.ToString()
            };
            yield return new Claim(kv.Name, value);
        }
    }
}
