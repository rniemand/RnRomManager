using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Http;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>B8–B11: <c>/api/collections/{id}/cover*</c> against MariaDB, a temp covers folder and a loopback image stub.</summary>
[Category("Integration")]
public class CollectionCoverEndpointsTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 8, 0, 32, 0, 64, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];

    private ApiFactory _factory = null!;
    private StubImageServer _stub = null!;
    private AuthClient _alice = null!;
    private AuthClient _bob = null!;
    private string _id = null!;

    private string CoversDir => _factory.Settings["Paths:Collections"]!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        // The stub is on 127.0.0.1, so loopback is allowed here.
        var policy = new ImageUrlPolicy(a => IPAddress.IsLoopback(a) || PublicAddress.IsPublic(a), TimeSpan.FromSeconds(1));
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync()) { TestServices = s => s.AddSingleton(policy) };
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        _stub = await StubImageServer.StartAsync();
        await _factory.ResetAsync();
        _alice = await SignInAsync("alice");
        _bob = await SignInAsync("bob");
        using var created = await _alice.PostAsync("/api/collections", new { name = "Mine" });
        _id = (await AuthClient.JsonAsync(created)).GetProperty("id").GetString()!;
    }

    [TearDown]
    public async Task TearDown()
    {
        _alice?.Dispose();
        _bob?.Dispose();
        await _stub.DisposeAsync();
    }

    private async Task<AuthClient> SignInAsync(string username)
    {
        var client = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, username);
        (await client.LoginAsync(username, TestUsers.Password)).Dispose();
        return client;
    }

    private static byte[] Png(uint width, uint height)
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0, 0, 0, 0, 0, 0,
            8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "IEND"u8, 0xAE, 0x42, 0x60, 0x82];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private Task<HttpResponseMessage> UploadAsync(AuthClient client, byte[] file, string? id = null)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", "anything.bin");
        return client.SendContentAsync(HttpMethod.Post, $"/api/collections/{id ?? _id}/cover", form);
    }

    private static async Task<string?> CoverUrlAsync(HttpResponseMessage response)
    {
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var url = (await AuthClient.JsonAsync(response)).GetProperty("coverUrl");
        return url.ValueKind == JsonValueKind.Null ? null : url.GetString();
    }

    private string[] Files() => Directory.GetFiles(CoversDir).Select(Path.GetFileName).Order().ToArray()!;

    [Test]
    public async Task Upload_Replace_Serve_AndRemove()
    {
        string pngUrl;
        using (var png = await UploadAsync(_alice, Png(300, 400)))
            pngUrl = (await CoverUrlAsync(png))!;
        Assert.That(pngUrl, Does.StartWith($"/api/collections/{_id}/cover?v="));
        using (var served = await _alice.GetAsync(pngUrl))
        {
            Assert.That(served.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(served.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/png"));
            Assert.That((served.Headers.CacheControl?.Private, served.Headers.CacheControl?.MaxAge), Is.EqualTo(((bool?)true, (TimeSpan?)TimeSpan.FromDays(1))));
            Assert.That(await served.Content.ReadAsByteArrayAsync(), Is.EqualTo(Png(300, 400)));
        }

        string jpgUrl;
        using (var jpg = await UploadAsync(_alice, Jpeg))
            jpgUrl = (await CoverUrlAsync(jpg))!;
        Assert.That(jpgUrl, Is.Not.EqualTo(pngUrl), "?v= changes on replace");
        Assert.That(Files(), Is.EqualTo(new[] { $"{_id}.jpg" }));
        using (var served = await _alice.GetAsync(jpgUrl))
            Assert.That(served.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));

        using (var removed = await _alice.SendAsync(HttpMethod.Delete, $"/api/collections/{_id}/cover", null, sendXsrf: true))
            Assert.That(await CoverUrlAsync(removed), Is.Null);
        Assert.That(Files(), Is.Empty);
        using (var gone = await _alice.GetAsync($"/api/collections/{_id}/cover"))
            Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Get_WhenTheFileIsMissing_Is404()
    {
        using (var png = await UploadAsync(_alice, Png(1, 1)))
            Assert.That(png.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        File.Delete(Path.Combine(CoversDir, $"{_id}.png"));

        using var response = await _alice.GetAsync($"/api/collections/{_id}/cover");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Upload_OverTheRequestLimit_Is413()
    {
        var huge = new byte[ImageFormats.MaxBytes + 128 * 1024];
        Png(1, 1).CopyTo(huge, 0);

        using var response = await UploadAsync(_alice, huge);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("PayloadTooLarge"));
    }

    [TestCase(new byte[] { 1, 2, 3 }, "The image must be a PNG or JPEG.")]
    [TestCase(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "The image's dimensions can't be read.")]
    public async Task Upload_BadImage_Is400OnFile(byte[] bytes, string message)
    {
        using var response = await UploadAsync(_alice, bytes);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").GetProperty("file")[0].GetString(), Is.EqualTo(message));
        Assert.That(Files(), Is.Empty);
    }

    [Test]
    public async Task FromUrl_DownloadsAndStoresTheImage()
    {
        _stub.Routes["/art.jpg"] = StubImageServer.Bytes(Jpeg);

        using var response = await _alice.PostAsync($"/api/collections/{_id}/cover/from-url", new { url = _stub.Url("/art.jpg").ToString() });

        Assert.That(await CoverUrlAsync(response), Is.Not.Null);
        Assert.That(Files(), Is.EqualTo(new[] { $"{_id}.jpg" }));
    }

    [Test]
    public async Task FromUrl_BadUrlOrBody_Is400OnUrl()
    {
        _stub.Routes["/text"] = StubImageServer.Bytes("hello"u8.ToArray(), "text/plain");

        foreach (var url in new[] { "ftp://example.com/a.png", "https://user:pw@example.com/a.png", "relative.png", _stub.Url("/text").ToString() })
        {
            using var response = await _alice.PostAsync($"/api/collections/{_id}/cover/from-url", new { url });
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), url);
            Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty("url", out _), Is.True, url);
        }
        Assert.That(Files(), Is.Empty);
    }

    [Test]
    public async Task AnotherUsersCollection_Is404_AndNothingIsDownloaded()
    {
        _stub.Routes["/art.jpg"] = StubImageServer.Bytes(Jpeg);
        using (var mine = await UploadAsync(_alice, Png(1, 1)))
            Assert.That(mine.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var get = await _bob.GetAsync($"/api/collections/{_id}/cover");
        using var upload = await UploadAsync(_bob, Jpeg);
        using var fromUrl = await _bob.PostAsync($"/api/collections/{_id}/cover/from-url", new { url = _stub.Url("/art.jpg").ToString() });
        using var remove = await _bob.SendAsync(HttpMethod.Delete, $"/api/collections/{_id}/cover", null, sendXsrf: true);

        foreach (var response in new[] { get, upload, fromUrl, remove })
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), response.RequestMessage!.Method.Method);
        Assert.That(_stub.Requests, Is.Empty);
        Assert.That(Files(), Is.EqualTo(new[] { $"{_id}.png" }));
    }
}
