using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Http;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>U1–U5: <c>POST /api/games/{id}/images/from-url</c> against a real Kestrel stub on loopback.</summary>
[Category("Integration")]
public class ImageUrlEndpointsTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] Tetris = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 8, 0, 32, 0, 64, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];

    private ApiFactory _factory = null!;
    private AuthClient _manager = null!;
    private StubImageServer _stub = null!;
    private Guid _game;
    // The stub is on 127.0.0.1, so the tests allow loopback unless a test wants the real check.
    private bool _realAddressCheck;

    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [SetUp]
    public async Task SetUp()
    {
        _realAddressCheck = false;
        _stub = await StubImageServer.StartAsync();
        var policy = new ImageUrlPolicy(a => (!_realAddressCheck && IPAddress.IsLoopback(a)) || PublicAddress.IsPublic(a), TimeSpan.FromSeconds(1));
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync()) { TestServices = s => s.AddSingleton(policy) };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        _manager = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "manager", Roles.Of(Roles.RomManager));
        (await _manager.LoginAsync("manager", TestUsers.Password)).Dispose();
        var cache = Service<DatCache>();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.GbaDat] = [],
            [IngestSeeder.N64Dat] = [],
            [IngestSeeder.NesDat] = [],
            [IngestSeeder.SnesDat] = [],
            [IngestSeeder.Atari2600Dat] = [],
            [IngestSeeder.ColecoVisionDat] = [],
            [IngestSeeder.GameGearDat] = [],
            [IngestSeeder.MasterSystemDat] = [],
            [IngestSeeder.MegaDriveDat] = [],
            [Gb] = [new FixtureGame("Tetris (World)", "Tetris (World).gb", Tetris)],
        });
        cache.Load();
        var path = Path.Combine(_factory.Root, "ingest", "gb", "t.gb");
        await File.WriteAllBytesAsync(path, Tetris);
        var file = new IngestFile("gb", path, "t.gb", Tetris.Length, DateTime.UtcNow.AddMinutes(-5));
        _game = (await Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default))!.GameId!.Value;
    }

    [TearDown]
    public async Task TearDown()
    {
        _manager.Dispose();
        _factory.Dispose();
        await _stub.DisposeAsync();
    }

    private static byte[] Png(uint width, uint height)
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0, 0, 0, 0, 0, 0,
            8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "IEND"u8, 0xAE, 0x42, 0x60, 0x82];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private Task<HttpResponseMessage> FromUrl(object body, Guid? game = null) =>
        _manager.PostAsync($"/api/games/{game ?? _game}/images/from-url", body);

    private Task<HttpResponseMessage> FromUrl(string url, string type = "BoxArt") => FromUrl(new { url, type });

    private static async Task<(string? Code, string? Title)> Problem(HttpResponseMessage response)
    {
        var body = await AuthClient.JsonAsync(response);
        return (body.GetProperty("code").GetString(), body.GetProperty("title").GetString());
    }

    private static async Task<string?> UrlError(HttpResponseMessage response)
    {
        var body = await AuthClient.JsonAsync(response);
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"), body.ToString());
        return body.GetProperty("errors").GetProperty("url")[0].GetString();
    }

    private async Task<JsonElement[]> StoredImages()
    {
        using var response = await _manager.GetAsync($"/api/games/{_game}/info-json");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("images").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private async Task<string?> AuditDetails()
    {
        await Service<AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        return await db.QuerySingleOrDefaultAsync<string?>("SELECT details FROM audit_events WHERE action = 'ImageAdded'");
    }

    [Test]
    public async Task Png_IsAdded_WithItsSourceUrl_AndAudited()
    {
        _stub.Routes["/art.png"] = StubImageServer.Bytes(Png(300, 400), "text/plain");
        var url = _stub.Url("/art.png").ToString();

        using var response = await FromUrl(url);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var image = (await AuthClient.JsonAsync(response)).GetProperty("images").EnumerateArray().Single();
        Assert.That((image.GetProperty("fileName").GetString(), image.GetProperty("source").GetString(), image.GetProperty("isCover").GetBoolean()),
            Is.EqualTo(("boxart-01.png", "upload", true)));
        var stored = (await StoredImages()).Single();
        Assert.That((stored.GetProperty("source").GetString(), stored.GetProperty("sourceUrl").GetString()), Is.EqualTo(("upload", url)));
        Assert.That(await AuditDetails(), Is.EqualTo($"boxart-01.png from {url}"));
        Assert.That(_stub.Requests, Is.EqualTo(new[] { "/art.png" }), "one request, no retries");
    }

    [Test]
    public async Task Jpeg_IsAddedAsASnap()
    {
        _stub.Routes["/s.jpg"] = StubImageServer.Bytes(Jpeg);

        using var response = await FromUrl(_stub.Url("/s.jpg").ToString(), "Snap");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var snap = (await AuthClient.JsonAsync(response)).GetProperty("images").EnumerateArray().Single(i => i.GetProperty("type").GetString() == "Snap");
        Assert.That(snap.GetProperty("fileName").GetString(), Is.EqualTo("snap-01.jpg"));
    }

    [Test]
    public async Task A1024CharacterUrl_IsStored()
    {
        _stub.Routes["/long.png"] = StubImageServer.Bytes(Png(5, 5));
        var prefix = _stub.Url("/long.png?q=").ToString();
        var url = prefix + new string('a', 1024 - prefix.Length);

        using var response = await FromUrl(url);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        Assert.That((await StoredImages()).Single().GetProperty("sourceUrl").GetString(), Is.EqualTo(url));
    }

    private static IEnumerable<TestCaseData> InvalidBodies()
    {
        yield return new TestCaseData(new { type = "BoxArt" }, "url").SetName("Missing url");
        yield return new TestCaseData(new { url = "/art.png", type = "BoxArt" }, "url").SetName("Relative url");
        yield return new TestCaseData(new { url = "ftp://127.0.0.1/art.png", type = "BoxArt" }, "url").SetName("ftp url");
        yield return new TestCaseData(new { url = "http://user:pw@127.0.0.1/art.png", type = "BoxArt" }, "url").SetName("User info");
        yield return new TestCaseData(new { url = "http://127.0.0.1/" + new string('a', 1025 - 17), type = "BoxArt" }, "url").SetName("1025 characters");
        yield return new TestCaseData(new { url = "http://127.0.0.1/art.png", type = "boxart" }, "type").SetName("Bad type");
    }

    [TestCaseSource(nameof(InvalidBodies))]
    public async Task Invalid_Is400OnTheField_WithoutARequest(object body, string field)
    {
        using var response = await FromUrl(body);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var json = await AuthClient.JsonAsync(response);
        Assert.That(json.GetProperty("errors").EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { field }), json.ToString());
        Assert.That(_stub.Requests, Is.Empty);
    }

    [Test]
    public async Task UnknownGame_Is404_WithoutARequest()
    {
        using var response = await FromUrl(new { url = _stub.Url("/art.png").ToString(), type = "BoxArt" }, Guid.NewGuid());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(_stub.Requests, Is.Empty);
    }

    [Test]
    public async Task NotFound_Is502_Http404()
    {
        using var response = await FromUrl(_stub.Url("/missing.png").ToString());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        Assert.That(await Problem(response), Is.EqualTo(("ImageDownloadFailed", "Couldn't download the image: HTTP 404.")));
    }

    [Test]
    public async Task ServerError_IsNotRetried()
    {
        _stub.Routes["/boom.png"] = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        };

        using var response = await FromUrl(_stub.Url("/boom.png").ToString());

        Assert.That(await Problem(response), Is.EqualTo(("ImageDownloadFailed", "Couldn't download the image: HTTP 500.")));
        Assert.That(_stub.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SlowHeaders_TimeOut()
    {
        _stub.Routes["/slow.png"] = async ctx => await Wait(ctx);

        using var response = await FromUrl(_stub.Url("/slow.png").ToString());

        Assert.That(await Problem(response), Is.EqualTo(("ImageDownloadFailed", "Couldn't download the image: timed out.")));
    }

    [Test]
    public async Task StalledBody_TimesOut()
    {
        _stub.Routes["/stall.png"] = async ctx =>
        {
            ctx.Response.ContentLength = 1000;
            await ctx.Response.Body.WriteAsync(Png(1, 1));
            await ctx.Response.Body.FlushAsync();
            await Wait(ctx);
        };

        using var response = await FromUrl(_stub.Url("/stall.png").ToString());

        Assert.That(await Problem(response), Is.EqualTo(("ImageDownloadFailed", "Couldn't download the image: timed out.")));
    }

    [Test]
    public async Task ConnectionClosedMidBody_CouldNotConnect()
    {
        _stub.Routes["/cut.png"] = async ctx =>
        {
            ctx.Response.ContentLength = 1000;
            await ctx.Response.Body.WriteAsync(Png(1, 1));
            await ctx.Response.Body.FlushAsync();
            ctx.Abort();
        };

        using var response = await FromUrl(_stub.Url("/cut.png").ToString());

        Assert.That(await Problem(response), Is.EqualTo(("ImageDownloadFailed", "Couldn't download the image: couldn't connect.")));
    }

    [Test]
    public async Task FourRedirects_AreTooMany()
    {
        for (var i = 1; i <= 4; i++)
        {
            var next = $"/r{i + 1}";
            _stub.Routes[$"/r{i}"] = ctx =>
            {
                ctx.Response.Redirect(next);
                return Task.CompletedTask;
            };
        }
        _stub.Routes["/r5"] = StubImageServer.Bytes(Png(1, 1));

        using var response = await FromUrl(_stub.Url("/r1").ToString());

        Assert.That(await Problem(response), Is.EqualTo(("ImageDownloadFailed", "Couldn't download the image: too many redirects.")));
        Assert.That(_stub.Requests, Is.EqualTo(new[] { "/r1", "/r2", "/r3", "/r4" }));
    }

    [Test]
    public async Task OversizedWithContentLength_IsAUrlError()
    {
        var big = new byte[ImageFormats.MaxBytes + 1];
        Png(1, 1).CopyTo(big, 0);
        _stub.Routes["/big.png"] = StubImageServer.Bytes(big);

        using var response = await FromUrl(_stub.Url("/big.png").ToString());

        Assert.That(await UrlError(response), Is.EqualTo("The image must be at most 8 MiB."));
    }

    [Test]
    public async Task OversizedChunked_IsAUrlError()
    {
        _stub.Routes["/chunked.png"] = async ctx =>
        {
            var chunk = new byte[1024 * 1024];
            Png(1, 1).CopyTo(chunk, 0);
            for (var i = 0; i < 9 && !ctx.RequestAborted.IsCancellationRequested; i++)
                await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
        };

        using var response = await FromUrl(_stub.Url("/chunked.png").ToString());

        Assert.That(await UrlError(response), Is.EqualTo("The image must be at most 8 MiB."));
    }

    [TestCase("", TestName = "Empty body → url error")]
    [TestCase("<html><body>Not an image</body></html>", TestName = "HTML body → url error")]
    public async Task NotAnImage_IsAUrlError(string body)
    {
        _stub.Routes["/page"] = StubImageServer.Bytes(System.Text.Encoding.UTF8.GetBytes(body), "image/png");

        using var response = await FromUrl(_stub.Url("/page").ToString());

        Assert.That(await UrlError(response), Is.EqualTo("The URL didn't return a PNG or JPEG image."));
    }

    [Test]
    public async Task SameImageTwice_Is409DuplicateImage()
    {
        _stub.Routes["/s.png"] = StubImageServer.Bytes(Png(2, 2));
        (await FromUrl(_stub.Url("/s.png").ToString(), "Snap")).Dispose();

        using var again = await FromUrl(_stub.Url("/s.png").ToString(), "Snap");

        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(again), Is.EqualTo("DuplicateImage"));
    }

    [Test]
    public async Task Loopback_WithTheRealCheck_Is400ImageUrlBlocked()
    {
        _realAddressCheck = true;
        _stub.Routes["/x.png"] = StubImageServer.Bytes(Png(1, 1));

        using var response = await FromUrl(_stub.Url("/x.png").ToString());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await Problem(response), Is.EqualTo(("ImageUrlBlocked", "The URL points to a private or local network address.")));
        Assert.That(_stub.Requests, Is.Empty);
    }

    [Test]
    public async Task WhileAJobRuns_Is409()
    {
        _stub.Routes["/j.png"] = StubImageServer.Bytes(Png(3, 3));
        using var held = Service<JobLock>().TryAcquire();

        using var response = await FromUrl(_stub.Url("/j.png").ToString());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("JobRunning"));
    }

    [Test]
    public async Task UserWithoutRomManager_Is403()
    {
        using var admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "admin-only", Roles.Of(Roles.Admin));
        (await admin.LoginAsync("admin-only", TestUsers.Password)).Dispose();

        using var response = await admin.PostAsync($"/api/games/{_game}/images/from-url", new { url = _stub.Url("/a.png").ToString(), type = "BoxArt" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(_stub.Requests, Is.Empty);
    }

    private static async Task Wait(HttpContext ctx)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The client gave up, as intended.
        }
    }
}
