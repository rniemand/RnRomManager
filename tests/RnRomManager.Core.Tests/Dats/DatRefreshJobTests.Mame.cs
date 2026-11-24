using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Dats;

/// <summary>Arcade D1, D4: the MAME part of a refresh, one download of <c>Sources:MameDatUrl</c>.</summary>
public partial class DatRefreshJobTests
{
    private const string MameUrl = "https://mame.test/mame2003-plus.xml";

    private static ISystemHandler MameHandler(string id)
    {
        var handler = Handler(id, DatStore.MameName);
        handler.DatSource.Returns(DatSource.Mame);
        return handler;
    }

    private void UseMame(params ISystemHandler[] extra)
    {
        ISystemHandler[] handlers = [Handler("gb", Gb), Handler("gbc", Gbc), MameHandler("mame2003"), .. extra];
        _systems.All.Returns(handlers);
    }

    private bool IsMame(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri == MameUrl;

    private void ServeMame(string xml) =>
        _override = request => IsMame(request) ? StubHttpHandler.Bytes(Encoding.UTF8.GetBytes(xml)) : null;

    private Task<object?> RunMameAsync(StubHttpHandler server, JobProgress? progress = null, TimeProvider? time = null) =>
        new DatRefreshJob(new HttpClient(server), new SourcesOptions { DatBaseUrl = BaseUrl + "/", MameDatUrl = MameUrl }, _paths, _cache, _systems,
                _audit, time ?? TimeProvider.System, NullLogger<DatRefreshJob>.Instance)
            .RunAsync(new JobContext(Guid.NewGuid(), progress ?? new JobProgress()), default);

    [Test]
    public async Task MameSystem_OneDownload_StagedWithManifestEntryAndVersion()
    {
        WriteLiveV1();
        UseMame();
        ServeMame(IngestSeeder.MinimalMame["MAME"]);
        var server = Server();
        var progress = new JobProgress();
        var time = new FixedTime(new DateTimeOffset(2026, 11, 24, 9, 8, 7, TimeSpan.Zero));

        var result = await RunMameAsync(server, progress, time);

        Assert.That(server.Requests.Count(IsMame), Is.EqualTo(1));
        Assert.That((progress.Current, progress.Total), Is.EqualTo((11, 11)), "5 per libretro system + 1");
        var manifest = DatManifest.Read(Path.Combine(_cache.Directory, DatManifest.FileName));
        var entry = manifest.Single(e => e.File.StartsWith("mame/"));
        Assert.That((entry.File, entry.DatName, entry.Version), Is.EqualTo(("mame/MAME.xml", "MAME", "2026-11-24T09:08:07Z")));
        Assert.That(File.ReadAllText(Path.Combine(_cache.Directory, "mame", "MAME.xml")), Is.EqualTo(IngestSeeder.MinimalMame["MAME"]));
        var versions = (IDictionary<string, string?>)result!.GetType().GetProperty("versions")!.GetValue(result)!;
        Assert.That(versions["MAME"], Is.EqualTo("2026-11-24T09:08:07Z"));
        Assert.That(_cache.For("mame2003")!.BySetName("rnmin")!.Description, Is.EqualTo("Rn Minimal (World)"));
    }

    [Test]
    public async Task MameParseFailure_KeepsTheOldSet()
    {
        WriteLiveV1();
        UseMame();
        ServeMame("<mame></mame>");

        var ex = await Assert.ThrowsAsync<DatParseException>(() => RunMameAsync(Server()));

        Assert.That(ex!.Message, Is.EqualTo("mame/MAME.xml: No <game> elements found."));
        AssertStillV1();
    }

    [Test]
    public async Task TwoMameSystems_AreRefusedBeforeAnyDownload()
    {
        var second = Handler("mame2", "MAME 2");
        second.DatSource.Returns(DatSource.Mame);
        UseMame(second);
        var server = Server();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunMameAsync(server));

        Assert.That(ex!.Message, Is.EqualTo("Systems 'mame2003' and 'mame2' both use Sources:MameDatUrl; nothing was refreshed."));
        Assert.That(server.Requests, Is.Empty);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
