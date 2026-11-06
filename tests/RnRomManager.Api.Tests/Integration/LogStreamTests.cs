using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RnRomManager.Api.Logging;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary><c>GET /api/admin/logs/stream</c> (L1–L2) against a real host and NLog.</summary>
[Category("Integration")]
public class LogStreamTests
{
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private User _boss = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => services.AddSingleton(new LogStreamOptions { KeepAlive = TimeSpan.FromMilliseconds(100) }),
        };
        _admin = await AuthClient.CreateAsync(_factory);
        _boss = await TestUsers.CreateAsync(_factory, "boss", Roles.Of(Roles.Admin));
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private void Log(string message) =>
        _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RnRomManager.Tests.LogStream").LogInformation("{Marker}", message);

    private Task<HttpResponseMessage> OpenAsync(long? lastEventId = null) =>
        _admin.SendAsync(HttpMethod.Get, "/api/admin/logs/stream", null, sendXsrf: false,
            headers: h =>
            {
                if (lastEventId is { } id)
                    h.Add("Last-Event-ID", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            },
            completion: HttpCompletionOption.ResponseHeadersRead);

    /// <summary>Reads lines until one contains <paramref name="needle"/>; returns every line read (null entry = stream ended).</summary>
    private static async Task<List<string?>> ReadUntilAsync(StreamReader reader, Func<string?, bool> stop)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var lines = new List<string?>();
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            lines.Add(line);
            if (line is null || stop(line))
                return lines;
        }
    }

    private static long IdBefore(List<string?> lines, string marker)
    {
        var data = lines.FindIndex(l => l is not null && l.StartsWith("data: ", StringComparison.Ordinal) && l.Contains(marker, StringComparison.Ordinal));
        return long.Parse(lines[data - 1]!["id: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Test]
    public async Task SendsBufferedThenLiveEventsAsSse()
    {
        Log("marker-before");
        using var response = await OpenAsync();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/event-stream"));
        Assert.That(response.Headers.CacheControl?.NoCache, Is.True);
        var buffered = await ReadUntilAsync(reader, l => l!.Contains("marker-before", StringComparison.Ordinal));
        Assert.That(buffered[^1], Does.StartWith("data: {").And.Contain("\"level\":\"Info\"").And.Contain("\"logger\":\"RnRomManager.Tests.LogStream\""));
        Assert.That(buffered[^2], Does.Match(@"^id: \d+$"));

        Log("marker-live");
        var live = await ReadUntilAsync(reader, l => l!.Contains("marker-live", StringComparison.Ordinal));
        Assert.That(live[^1], Is.Not.Null);
    }

    [Test]
    public async Task HonoursLastEventId()
    {
        Log("marker-one");
        long firstId;
        using (var first = await OpenAsync())
        using (var reader = new StreamReader(await first.Content.ReadAsStreamAsync()))
            firstId = IdBefore(await ReadUntilAsync(reader, l => l!.Contains("marker-one", StringComparison.Ordinal)), "marker-one");
        Log("marker-two");

        using var resumed = await OpenAsync(firstId);
        using var resumedReader = new StreamReader(await resumed.Content.ReadAsStreamAsync());
        var lines = await ReadUntilAsync(resumedReader, l => l!.Contains("marker-two", StringComparison.Ordinal));

        Assert.That(lines, Has.None.Contains("marker-one"));
        Assert.That(IdBefore(lines, "marker-two"), Is.GreaterThan(firstId));
    }

    [Test]
    public async Task EndsAtTheNextKeepAliveOnceTheUserIsDisabled()
    {
        using var response = await OpenAsync();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var users = _factory.Services.GetRequiredService<IUserRepository>();
        await ReadUntilAsync(reader, l => l == ": keep-alive");

        await users.UpdateAsync(_boss with { IsDisabled = true }, CancellationToken.None);
        var rest = await ReadUntilAsync(reader, _ => false);

        Assert.That(rest[^1], Is.Null, "the stream ended");
    }
}
