using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Library;
using RnRomManager.Data.Repositories;
using RnRomManager.Core.Users;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Collections discovery B5–B6: <c>/api/discovery/publishers/logo</c> against MariaDB, with two admins (writes are admin-only, U3).</summary>
[Category("Integration")]
public class PublisherLogoEndpointsTests
{
    // The discovery index's own clock: moved past its lifetime before each test and after library changes.
    private readonly FakeTimeProvider _indexClock = new();
    private ApiFactory _factory = null!;
    private AuthClient _alice = null!;
    private AuthClient _bob = null!;
    private int _seed;

    private static readonly string Nintendo = Key("nintendo");

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => services.AddSingleton<IDiscoveryRepository>(sp => new DiscoveryRepository(
                sp.GetRequiredService<IDbConnectionFactory>(), sp.GetRequiredService<IGameRepository>(), _indexClock)),
        };
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        await _factory.ResetAsync();
        _alice = await SignInAsync("alice");
        _bob = await SignInAsync("bob");
        await AddGameAsync("Tetris", ["Nintendo"]);
        ExpireIndex();
    }

    [TearDown]
    public void TearDown()
    {
        _alice?.Dispose();
        _bob?.Dispose();
    }

    private void ExpireIndex() => _indexClock.Advance(IDiscoveryRepository.IndexLifetime);

    private static string Key(string normalized) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    private static string Url(string key) => $"/api/discovery/publishers/logo?key={key}";

    private async Task<AuthClient> SignInAsync(string username, bool siteUser = false)
    {
        var client = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, username, siteUser ? null : Roles.Of(Roles.Admin));
        (await client.LoginAsync(username, TestUsers.Password)).Dispose();
        return client;
    }

    private async Task<Guid> AddGameAsync(string title, string[] publishers)
    {
        var game = Game(title, Rom($"{title}.gb", RomBytes(++_seed))) with { Publishers = publishers };
        await _factory.Services.GetRequiredService<ILibraryMirror>()
            .ReplaceAsync(new MirrorEntry(game, $"gb/{title[0]}/{title}.zip", 100, When.UtcDateTime), default);
        return game.GameId;
    }

    private static byte[] Png(uint width, uint height, int size = 0)
    {
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0, 0, 0, 0, 0, 0,
            8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "IEND"u8, 0xAE, 0x42, 0x60, 0x82];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), height);
        var bytes = new byte[Math.Max(size, header.Length)];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Jpeg(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, 0xC0, 0x00, 0x11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
        1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        0xFF, 0xDA, 0x00, 0x02,
    ];

    private static MultipartFormDataContent Form(params (string Field, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (field, bytes) in files)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, field, "logo.bin");
        }
        return form;
    }

    private static Task<HttpResponseMessage> PutAsync(AuthClient client, string key, byte[] file) =>
        client.SendContentAsync(HttpMethod.Put, Url(key), Form(("file", file)));

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }

    private async Task<JsonElement> PublisherAsync(AuthClient client, string key)
    {
        using var response = await client.GetAsync("/api/discovery/publishers?pageSize=200");
        return (await AuthClient.JsonAsync(response)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key);
    }

    private sealed record AuditRow(string Action, string? Username, string? Details);

    private async Task<List<AuditRow>> AuditRowsAsync()
    {
        await _factory.Services.GetRequiredService<AuditWriter>().FlushAsync();
        await using var db = _factory.Services.GetRequiredService<IDbConnectionFactory>().Create();
        return (await db.QueryAsync<AuditRow>("""
            SELECT action AS Action, username AS Username, details AS Details FROM audit_events
            WHERE action LIKE 'PublisherLogo%' ORDER BY id
            """)).AsList();
    }

    [Test]
    public async Task Put_Serve_ReplaceByAnotherUser_AndRemove()
    {
        Assert.That((await PublisherAsync(_alice, Nintendo)).GetProperty("logoUrl").ValueKind, Is.EqualTo(JsonValueKind.Null));

        var png = Png(300, 120);
        Assert.That(await StatusAsync(PutAsync(_alice, Nintendo, png)), Is.EqualTo(HttpStatusCode.NoContent));
        var firstUrl = (await PublisherAsync(_bob, Nintendo)).GetProperty("logoUrl").GetString();
        Assert.That(firstUrl, Does.StartWith($"{Url(Nintendo)}&v="));
        using (var served = await _bob.GetAsync(firstUrl!))
        {
            Assert.That(served.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(served.Content.Headers.ContentType!.MediaType, Is.EqualTo("image/png"));
            var cache = served.Headers.CacheControl!;
            Assert.That((cache.Private, cache.NoCache, cache.MaxAge, cache.Public), Is.EqualTo((true, true, (TimeSpan?)null, false)));
            Assert.That(served.Headers.GetValues("X-Content-Type-Options"), Is.EqualTo(new[] { "nosniff" }));
            Assert.That(await served.Content.ReadAsByteArrayAsync(), Is.EqualTo(png));
        }

        var jpeg = Jpeg(64, 64);
        Assert.That(await StatusAsync(PutAsync(_bob, Nintendo, jpeg)), Is.EqualTo(HttpStatusCode.NoContent), "shared: any user replaces it");
        var secondUrl = (await PublisherAsync(_alice, Nintendo)).GetProperty("logoUrl").GetString();
        Assert.That(secondUrl, Is.Not.EqualTo(firstUrl), "a new version in the URL");
        using (var served = await _alice.GetAsync(Url(Nintendo)))
        {
            Assert.That(served.Content.Headers.ContentType!.MediaType, Is.EqualTo("image/jpeg"));
            Assert.That(await served.Content.ReadAsByteArrayAsync(), Is.EqualTo(jpeg));
        }

        Assert.That(await StatusAsync(_alice.SendAsync(HttpMethod.Delete, Url(Nintendo), null, sendXsrf: true)), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await StatusAsync(_alice.GetAsync(Url(Nintendo))), Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await PublisherAsync(_alice, Nintendo)).GetProperty("logoUrl").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(await StatusAsync(_bob.SendAsync(HttpMethod.Delete, Url(Nintendo), null, sendXsrf: true)), Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task EverySuccessfulChange_IsAudited()
    {
        await StatusAsync(PutAsync(_alice, Nintendo, Png(10, 10)));
        await StatusAsync(PutAsync(_bob, Nintendo, Png(20, 20)));
        await StatusAsync(PutAsync(_bob, Nintendo, [1, 2, 3]));
        await StatusAsync(_alice.SendAsync(HttpMethod.Delete, Url(Nintendo), null, sendXsrf: true));
        await StatusAsync(_alice.SendAsync(HttpMethod.Delete, Url(Nintendo), null, sendXsrf: true));

        var details = $"Nintendo (key {Nintendo})";
        Assert.That(await AuditRowsAsync(), Is.EqualTo(new[]
        {
            new AuditRow(AuditActions.PublisherLogoReplaced, "alice", details),
            new AuditRow(AuditActions.PublisherLogoReplaced, "bob", details),
            new AuditRow(AuditActions.PublisherLogoRemoved, "alice", details),
        }), "failed writes aren't audited");
    }

    [Test]
    public async Task Override_OutlivesThePublisherLeavingTheIndex()
    {
        await StatusAsync(PutAsync(_alice, Nintendo, Png(10, 10)));
        await AddGameAsync("Tetris", ["Bullet-Proof Software"]);
        ExpireIndex();

        Assert.That(await StatusAsync(_alice.GetAsync(Url(Nintendo))), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await StatusAsync(PutAsync(_alice, Nintendo, Png(10, 10))), Is.EqualTo(HttpStatusCode.NotFound),
            "only publishers in the index get a new override");

        await AddGameAsync("Tetris", ["NINTENDO"]);
        ExpireIndex();
        Assert.That((await PublisherAsync(_alice, Nintendo)).GetProperty("logoUrl").GetString(), Does.StartWith(Url(Nintendo)),
            "back in the index, the kept override shows again");

        await AddGameAsync("Tetris", ["Bullet-Proof Software"]);
        ExpireIndex();
        Assert.That(await StatusAsync(_alice.SendAsync(HttpMethod.Delete, Url(Nintendo), null, sendXsrf: true)), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await AuditRowsAsync()).Last().Details, Is.EqualTo($"key {Nintendo}"));
    }

    [Test]
    public async Task UnknownPublisher_Is404_AndNothingIsStored()
    {
        Assert.That(await StatusAsync(PutAsync(_alice, Key("sega"), Png(10, 10))), Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await StatusAsync(_alice.GetAsync(Url(Key("sega")))), Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await AuditRowsAsync(), Is.Empty);
    }

    [Test]
    public async Task GenreGroups_HaveNoLogoUrl()
    {
        using var response = await _alice.GetAsync("/api/discovery/genres");
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("items").EnumerateArray().All(i => !i.TryGetProperty("logoUrl", out _)));
    }

    [TestCase("")]
    [TestCase("?key=")]
    [TestCase("?key=NINTENDO")]
    [TestCase("?key=C0A4942143E872CD1AE29FC759E04526DE2E909AC1732734D38550A29C2E2516")]
    public async Task InvalidKey_Is400(string query)
    {
        const string route = "/api/discovery/publishers/logo";
        Assert.That(await StatusAsync(_alice.GetAsync(route + query)), Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await StatusAsync(_alice.SendContentAsync(HttpMethod.Put, route + query, Form(("file", Png(1, 1))))), Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await StatusAsync(_alice.SendAsync(HttpMethod.Delete, route + query, null, sendXsrf: true)), Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private static IEnumerable<TestCaseData> InvalidUploads()
    {
        yield return new TestCaseData((Func<HttpContent>)(() => Form(("file", Png(1, 1)), ("file", Png(2, 2)))), "Send exactly one image in the file field.")
            .SetName("Two files in file");
        yield return new TestCaseData((Func<HttpContent>)(() => Form(("file", Png(1, 1)), ("other", Png(2, 2)))), "Send exactly one image in the file field.")
            .SetName("A second file in another field");
        yield return new TestCaseData((Func<HttpContent>)(() => Form(("logo", Png(1, 1)))), "Send exactly one image in the file field.")
            .SetName("No file field");
        yield return new TestCaseData((Func<HttpContent>)(() => Form(("file", []))), "Choose an image file.").SetName("Empty file");
        yield return new TestCaseData((Func<HttpContent>)(() => Form(("file", "<svg/>"u8.ToArray()))), "The logo must be a PNG or JPEG image.")
            .SetName("Not a PNG or JPEG");
        yield return new TestCaseData((Func<HttpContent>)(() => Form(("file", Png(10, 10, PublisherLogoEndpoints.MaxBytes + 1)))),
            "The logo must be at most 512 KiB.").SetName("Over 512 KiB within the request limit");
        yield return new TestCaseData((Func<HttpContent>)(() => Form(("file", Png(2049, 10)))), "The logo must be at most 2048 pixels wide and high.")
            .SetName("Too wide");
        yield return new TestCaseData((Func<HttpContent>)(() => Form(("file", Jpeg(10, 2049)))), "The logo must be at most 2048 pixels wide and high.")
            .SetName("Too high");
        yield return new TestCaseData((Func<HttpContent>)(() => new ByteArrayContent(Png(1, 1)) { Headers = { ContentType = new("image/png") } }),
            "Send the logo as multipart/form-data.").SetName("Not a form");
    }

    [TestCaseSource(nameof(InvalidUploads))]
    public async Task InvalidUpload_Is400_AndKeepsTheCurrentLogo(Func<HttpContent> content, string message)
    {
        var current = Png(5, 5);
        await StatusAsync(PutAsync(_alice, Nintendo, current));

        using var response = await _alice.SendContentAsync(HttpMethod.Put, Url(Nintendo), content());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").GetProperty("file")[0].GetString(), Is.EqualTo(message));
        using var served = await _alice.GetAsync(Url(Nintendo));
        Assert.That(await served.Content.ReadAsByteArrayAsync(), Is.EqualTo(current));
    }

    [Test]
    public async Task MaximumLogo_IsAccepted()
    {
        Assert.That(await StatusAsync(PutAsync(_alice, Nintendo, Png(2048, 2048, PublisherLogoEndpoints.MaxBytes))), Is.EqualTo(HttpStatusCode.NoContent));
    }

    [TestCase(false, TestName = "Oversized with Content-Length → 413")]
    [TestCase(true, TestName = "Oversized chunked → 413")]
    public async Task OversizedRequest_Is413(bool chunked)
    {
        HttpContent form = Form(("file", Png(10, 10, PublisherLogoEndpoints.RequestLimit + 1)));
        if (chunked)
            form = new UnknownLengthContent(form);

        using var response = await _alice.SendContentAsync(HttpMethod.Put, Url(Nintendo), form);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("PayloadTooLarge"));
        Assert.That(await StatusAsync(_alice.GetAsync(Url(Nintendo))), Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task SiteUser_Is403OnWrites_ButSeesTheLogo()
    {
        await StatusAsync(PutAsync(_alice, Nintendo, Png(5, 5)));
        using var site = await SignInAsync("carol", siteUser: true);

        Assert.That(await StatusAsync(site.GetAsync(Url(Nintendo))), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await StatusAsync(PutAsync(site, Nintendo, Png(1, 1))), Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await StatusAsync(site.SendAsync(HttpMethod.Delete, Url(Nintendo), null, sendXsrf: true)), Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Anonymous_Is401()
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);

        Assert.That(await StatusAsync(anonymous.GetAsync(Url(Nintendo))), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await StatusAsync(PutAsync(anonymous, Nintendo, Png(1, 1))), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await StatusAsync(anonymous.SendAsync(HttpMethod.Delete, Url(Nintendo), null, sendXsrf: true)), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await AuditRowsAsync(), Is.Empty);
    }

    [Test]
    public async Task Writes_NeedTheAntiforgeryToken()
    {
        using (var response = await _alice.SendContentAsync(HttpMethod.Put, Url(Nintendo), Form(("file", Png(1, 1))), sendXsrf: false))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("AntiforgeryInvalid"));
        }
        Assert.That(await StatusAsync(_alice.GetAsync(Url(Nintendo))), Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>The inner content without a length, so it's sent chunked.</summary>
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly HttpContent _inner;

        public UnknownLengthContent(HttpContent inner)
        {
            _inner = inner;
            foreach (var header in inner.Headers.Where(h => h.Key != "Content-Length"))
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
