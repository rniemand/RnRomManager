namespace RnRomManager.Api.Cli;

/// <summary><c>--healthcheck</c>: GET /health/live on the local port, exit 0 when healthy (spec §17.1).</summary>
public static class HealthCheckCommand
{
    public const int DefaultPort = 8080;

    public static async Task<int> RunAsync(string? httpPorts)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{PortFrom(httpPorts)}/health/live");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    /// <summary>First entry of <c>ASPNETCORE_HTTP_PORTS</c> (<c>;</c>-separated), or 8080.</summary>
    public static int PortFrom(string? httpPorts)
    {
        var first = httpPorts?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, out var port) && port is > 0 and <= 65535 ? port : DefaultPort;
    }
}
