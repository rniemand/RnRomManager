using System.Net;
using NSubstitute;
using RnRomManager.Core.Dats;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Dats;

/// <summary>Disc systems B1/B2: a handler's main DAT folder and the metadat kinds it lists.</summary>
public partial class DatRefreshJobTests
{
    private const string Psx = "Sony - PlayStation";

    private void UseRedumpOnly(params string[] metadatKinds)
    {
        var handler = Handler("psx", Psx);
        handler.DatFolder.Returns(DatKind.Redump);
        handler.MetadatKinds.Returns(metadatKinds);
        handler.RomExtensions.Returns([".bin", ".cue"]);
        _systems.All.Returns([handler]);
        FixtureDatWriter.Write(_remote, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [Psx] = [new FixtureGame("Disc Runner (USA)", "Disc Runner (USA) (Track 1).bin", new SyntheticRomBuilder { Seed = 9 }.Build())],
        }, version: "v2", layouts: new Dictionary<string, FixtureDatLayout> { [Psx] = new(DatKind.Redump, [DatKind.Genre]) });
    }

    [Test]
    public async Task Redump_NoMetadat_FetchesOneUrl_AndListsItsVersion()
    {
        UseRedumpOnly();
        var server = Server();

        var result = await RunAsync(server);

        Assert.That(server.Requests.Select(r => r.RequestUri!.AbsoluteUri), Is.EqualTo(new[] { $"{BaseUrl}/redump/Sony%20-%20PlayStation.dat" }));
        var versions = (IDictionary<string, string?>)result!.GetType().GetProperty("versions")!.GetValue(result)!;
        Assert.That(versions, Is.EqualTo(new Dictionary<string, string?> { [Psx] = "v2" }));
        Assert.That(_cache.For("psx")!.Version, Is.EqualTo("v2"));
        Assert.That(_cache.For("psx")!.Roms, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Redump_HeaderNamingAnotherSystem_FailsTheRefresh()
    {
        UseRedumpOnly();
        File.WriteAllText(Path.Combine(_remote, "redump", Psx + ".dat"), "clrmamepro ( name \"Sega - Saturn\" version \"v2\" )");

        var ex = await Assert.ThrowsAsync<DatParseException>(() => RunAsync(Server()));

        Assert.That(ex!.Message, Does.StartWith($"redump/{Psx}.dat"));
        Assert.That(_cache.For("psx"), Is.Null);
    }

    [Test]
    public async Task ListedMetadatKind404_FailsTheRefresh()
    {
        UseRedumpOnly(DatKind.Genre);
        File.Delete(Path.Combine(_remote, "genre", Psx + ".dat"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(Server()));

        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
