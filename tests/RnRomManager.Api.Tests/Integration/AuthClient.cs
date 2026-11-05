using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>
/// Plays the SPA: keeps the latest <c>XSRF-TOKEN</c> cookie value and sends it as <c>X-XSRF-TOKEN</c> on mutations
/// (spec §9.2). One instance = one browser (its own cookie jar).
/// </summary>
public sealed class AuthClient(HttpClient http) : IDisposable
{
    private string? _xsrf;

    public static async Task<AuthClient> CreateAsync(ApiFactory factory)
    {
        var client = new AuthClient(factory.CreateClient());
        (await client.GetAsync("/api/auth/antiforgery")).Dispose();
        return client;
    }

    public async Task<HttpResponseMessage> GetAsync(string url) => Capture(await http.GetAsync(url));

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, bool sendXsrf = true) =>
        SendAsync(HttpMethod.Post, url, body, sendXsrf);

    public Task<HttpResponseMessage> PatchAsync(string url, object? body = null) => SendAsync(HttpMethod.Patch, url, body, true);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, bool sendXsrf,
        Action<HttpRequestHeaders>? headers = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (sendXsrf && _xsrf is not null)
            request.Headers.Add("X-XSRF-TOKEN", _xsrf);
        headers?.Invoke(request.Headers);
        return Capture(await http.SendAsync(request));
    }

    public Task<HttpResponseMessage> LoginAsync(string username, string password, bool sendXsrf = true) =>
        PostAsync("/api/auth/login", new { username, password }, sendXsrf);

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("code").GetString();

    private HttpResponseMessage Capture(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.FirstOrDefault(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)) is { } cookie)
        {
            _xsrf = Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..cookie.IndexOf(';', StringComparison.Ordinal)]);
        }
        return response;
    }

    public void Dispose() => http.Dispose();
}
