using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace RnRomManager.Api.Tests;

public class AppHostTests
{
    private const string IndexHtml = "<!doctype html><title>spa</title>";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new ApiFactory();
        var webRoot = Path.Combine(_factory.Root, "wwwroot");
        Directory.CreateDirectory(Path.Combine(webRoot, "_app", "immutable"));
        File.WriteAllText(Path.Combine(webRoot, "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(webRoot, "_app", "immutable", "x.js"), "console.log(1);");
        _factory.Settings[WebHostDefaults.WebRootKey] = webRoot;
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task HealthLive_Returns200WithoutBody()
    {
        using var response = await _client.GetAsync("/health/live");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
    }

    [TestCase("/api/nope")]
    [TestCase("/api")]
    [TestCase("/api/games/1/whatever")]
    public async Task UnknownApiRoute_Returns404ProblemDetails(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(json.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(404));
        Assert.That(json.RootElement.GetProperty("code").GetString(), Is.EqualTo("NotFound"));
        Assert.That(json.RootElement.GetProperty("traceId").GetString(), Is.Not.Empty);
    }

    [Test]
    public async Task UnknownApiRoute_AnyMethod_Returns404ProblemDetails()
    {
        using var response = await _client.PostAsync("/api/nope", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
    }

    [Test]
    public async Task UnknownHealthRoute_Returns404NotSpa()
    {
        using var response = await _client.GetAsync("/health/nope");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("spa"));
    }

    [TestCase("/")]
    [TestCase("/some/route")]
    [TestCase("/games/12")]
    public async Task UnknownRoute_ServesIndexHtmlWithNoCache(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(IndexHtml));
        Assert.That(response.Headers.CacheControl?.NoCache, Is.True);
    }

    [Test]
    public async Task ImmutableAsset_GetsImmutableCacheHeader()
    {
        using var response = await _client.GetAsync("/_app/immutable/x.js");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.CacheControl?.ToString(), Is.EqualTo("public, max-age=31536000, immutable"));
    }

    [Test]
    public async Task MissingAssetWithExtension_Returns404()
    {
        using var response = await _client.GetAsync("/_app/immutable/missing.js");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
