using System.Net;
using NSubstitute;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.Core.Tests.Library;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Media;

/// <summary>Spec T8: a TitleDb system's media comes from its entries' CDN URLs, PNG or JPEG, stored as <c>titledb</c>.</summary>
public partial class MediaServiceTests
{
    private const string Cdn = "https://img-eshop.cdn.nintendo.net/i/";
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 8, 0, 32, 0, 64, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];

    private static TitleDbRow Entry(string id, string? boxArt, string? snap = null) => new(id, "Orbit", null, null, null, [], [], boxArt, snap);

    /// <summary>The handler becomes a TitleDb system whose store holds <paramref name="rows"/>.</summary>
    private void UseTitleDb(params TitleDbRow[] rows)
    {
        _handler.DatSource.Returns(DatSource.TitleDb);
        _dats.Publish(new Dictionary<string, DatStore> { ["gb"] = new DatStore(rows, "2026.11.10") });
    }

    private static string GameName(string id) => $"Orbit [{id}]";

    [Test]
    public async Task TitleDb_PngBoxArtAndJpegSnap_CarryTheTitleDbSourceAndUrl()
    {
        UseTitleDb(Entry("0100000000002000", Cdn + "box.png", Cdn + "snap.jpg"));
        _routes[Cdn + "box.png"] = Png();
        _routes[Cdn + "snap.jpg"] = () => StubHttpHandler.Bytes(JpegBytes);

        var result = await FindAsync(GameName("0100000000002000"));

        Assert.That((result.BoxArt.FileName, result.BoxArt.Source, result.BoxArt.SourceUrl, result.BoxArt.IsDefault),
            Is.EqualTo(("boxart-01.png", (string?)InfoJsonImage.SourceTitleDb, (string?)(Cdn + "box.png"), false)));
        Assert.That((result.Snap!.FileName, result.Snap.Source, result.Snap.SourceUrl, result.Snap.Width, result.Snap.Height),
            Is.EqualTo(("snap-01.jpg", (string?)InfoJsonImage.SourceTitleDb, (string?)(Cdn + "snap.jpg"), 64, 32)));
        Assert.That(result.Snap.ToInfoJson(DateTimeOffset.UnixEpoch, false).Source, Is.EqualTo(InfoJsonImage.SourceTitleDb));
    }

    [Test]
    public async Task TitleDb_NamesAreTriedInOrder_ByTheIdTheyEndWith()
    {
        UseTitleDb(Entry("0100000000002000", Cdn + "missing.png"), Entry("0100000000004000", Cdn + "second.png"));
        _routes[Cdn + "second.png"] = Png();

        var result = await FindAsync(null, "No id here", GameName("0100000000009000"), GameName("0100000000002000"), GameName("0100000000004000"));

        Assert.That(result.BoxArt.SourceUrl, Is.EqualTo(Cdn + "second.png"));
        Assert.That(_server.Requests.Select(r => r.RequestUri!.AbsoluteUri), Is.EqualTo(new[] { Cdn + "missing.png", Cdn + "second.png" }),
            "names without an id or entry make no request");
    }

    [TestCase("http://img-eshop.cdn.nintendo.net/i/box.png", TestName = "TitleDb_NonCdnUrl_IsNeverRequested http")]
    [TestCase("https://example.com/box.png", TestName = "TitleDb_NonCdnUrl_IsNeverRequested other host")]
    [TestCase("https://img-eshop.cdn.nintendo.net:8443/box.png", TestName = "TitleDb_NonCdnUrl_IsNeverRequested other port")]
    public async Task TitleDb_NonCdnUrl_IsNeverRequested(string url)
    {
        UseTitleDb(Entry("0100000000002000", url)); // built directly, past the compact reader's own check
        _routes[url] = Png();

        var result = await FindAsync(GameName("0100000000002000"));

        AssertDefault(result.BoxArt);
        Assert.That(_server.Requests, Is.Empty);
    }

    [Test]
    public async Task TitleDb_ACdnRedirect_IsANeutralMiss_AfterTwoFailures()
    {
        UseTitleDb(Entry("0100000000002000", Cdn + "a.png"), Entry("0100000000004000", Cdn + "b.png"), Entry("0100000000006000", Cdn + "c.png"));
        _routes[Cdn + "a.png"] = () => StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable);
        _routes[Cdn + "b.png"] = () => StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable);
        _routes[Cdn + "c.png"] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.com/x.png") } };

        var result = await FindAsync(GameName("0100000000002000"), GameName("0100000000004000"), GameName("0100000000006000"));

        AssertDefault(result.BoxArt);
        Assert.That(_server.Requests, Has.Count.EqualTo(3), "the redirect isn't followed");
        Assert.That(_breaker.IsOpen, Is.False);
        Assert.That(_breaker.RecordFailure(), Is.True, "the streak was still two: one more opens it");
    }

    [Test]
    public async Task TitleDb_EmptyThumbnailBaseUrl_RequestsNothing()
    {
        UseTitleDb(Entry("0100000000002000", Cdn + "box.png"));
        _routes[Cdn + "box.png"] = Png();

        var result = await Service(baseUrl: "").FindAsync(_handler, [GameName("0100000000002000")], _breaker, CancellationToken.None);

        AssertDefault(result.BoxArt);
        Assert.That(_server.Requests, Is.Empty);
    }

    [Test]
    public async Task Libretro_StillRejectsJpeg()
    {
        _routes[GbBox + "Tetris%20%28World%29.png"] = () => StubHttpHandler.Bytes(JpegBytes);

        var result = await FindAsync("Tetris (World)");

        AssertDefault(result.BoxArt);
        Assert.That(_server.Requests.Select(r => r.RequestUri!.AbsoluteUri), Does.Contain(GbBox + "Tetris%20%28World%29.png"), "served, then refused");
    }
}
