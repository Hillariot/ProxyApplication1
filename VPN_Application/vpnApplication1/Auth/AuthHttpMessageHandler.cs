// Net/AuthHttpMessageHandler.cs
public sealed class AuthHttpMessageHandler : DelegatingHandler
{
    private readonly IAuthService _auth;

    public AuthHttpMessageHandler(IAuthService auth) => _auth = auth;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _auth.EnsureValidAccessTokenAsync();
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // на случай истечения между EnsureValid... и отправкой запроса
            token = await _auth.EnsureValidAccessTokenAsync();
            if (!string.IsNullOrEmpty(token))
            {
                request = Clone(request); // повторная попытка
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                response.Dispose();
                return await base.SendAsync(request, ct);
            }
        }

        return response;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        // контент
        if (req.Content is not null)
        {
            var ms = new MemoryStream();
            req.Content.CopyToAsync(ms).GetAwaiter().GetResult();
            ms.Position = 0;
            clone.Content = new StreamContent(ms);
            foreach (var h in req.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        // заголовки
        foreach (var h in req.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        clone.Version = req.Version;
        return clone;
    }
}
