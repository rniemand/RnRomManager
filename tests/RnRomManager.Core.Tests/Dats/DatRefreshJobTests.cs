using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Dats;

/// <summary>Spec §10.1 "Refresh DATs": all-or-nothing swap, failures and cancel keep the old set.</summary>
public class DatRefreshJobTests
{
    private const string Gb = "Nintendo - Game Boy";
    private const string Gbc = "Nintendo - Game Boy Color";
    private const string BaseUrl = "https://dats.test/metadat";

    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private ISystemRegistry _systems = null!;
    private DatCache _cache = null!;
    private string _remote = null!;
    private Func<HttpRequestMessage, HttpResponseMessage?> _override = _ => null;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Cache = _dir.Combine("cache") };
        ISystemHandler[] handlers = [Handler("gb", Gb), Handler("gbc", Gbc)];
        _systems = Substitute.For<ISystemRegistry>();
        _systems.All.Returns(handlers);
        _cache = new DatCache(Options.Create(_paths), _systems, NullLogger<DatCache>.Instance);

        // What the "server" has: version v2.
        _remote = _dir.Combine("remote");
        FixtureDatWriter.Write(_remote, Games(), version: "v2");
        _override = _ => null;
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static ISystemHandler Handler(string id, string datName)
    {
        var handler = Substitute.For<ISystemHandler>();
        handler.Id.Returns(id);
        handler.DatName.Returns(datName);
        return handler;
    }

    private static Dictionary<string, IReadOnlyList<FixtureGame>> Games() => new()
    {
        [Gb] = [new FixtureGame("Tetris (World) (Rev 1)", "Tetris (World) (Rev 1).gb", new SyntheticRomBuilder { Seed = 1 }.Build()) { Developers = ["Nintendo"] }],
        [Gbc] = [new FixtureGame("Pokemon - Gold Version (USA)", "Pokemon - Gold Version (USA).gbc", new SyntheticRomBuilder { Seed = 2 }.Build())],
    };

    private void WriteLiveV1() => FixtureDatWriter.Write(_cache.Directory, Games(), version: "v1");

    private StubHttpHandler Server() => new(request =>
    {
        if (_override(request) is { } response)
            return response;
        var relative = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath["/metadat/".Length..]);
        var file = Path.Combine(_remote, relative);
        return File.Exists(file) ? StubHttpHandler.Bytes(File.ReadAllBytes(file)) : StubHttpHandler.Status(HttpStatusCode.NotFound);
    });

    private DatRefreshJob Job(StubHttpHandler server) => new(
        new HttpClient(server), new SourcesOptions { DatBaseUrl = BaseUrl + "/" }, _paths, _cache, _systems, TimeProvider.System,
        NullLogger<DatRefreshJob>.Instance);

    private Task<object?> RunAsync(StubHttpHandler server, JobProgress? progress = null, CancellationToken ct = default) =>
        Job(server).RunAsync(new JobContext(Guid.NewGuid(), progress ?? new JobProgress()), ct);

    private void AssertStillV1()
    {
        _cache.Load();
        Assert.That(_cache.For("gb")!.Version, Is.EqualTo("v1"));
        Assert.That(_cache.For("gbc")!.Version, Is.EqualTo("v1"));
        Assert.That(Directory.Exists(Path.Combine(_paths.Cache, CrashRecovery.WorkFolder))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(_paths.Cache, CrashRecovery.WorkFolder))
            : [], Is.Empty, "work dir cleaned up");
    }

    [Test]
    public async Task Success_SwapsAllTenFilesAndReloads()
    {
        WriteLiveV1();
        _cache.Load();
        var server = Server();
        var progress = new JobProgress();

        await RunAsync(server, progress);

        Assert.That(server.Requests, Has.Count.EqualTo(10));
        Assert.That(server.Requests.Select(r => r.RequestUri!.AbsoluteUri), Does.Contain($"{BaseUrl}/no-intro/Nintendo%20-%20Game%20Boy.dat"));
        Assert.That(_cache.For("gb")!.Version, Is.EqualTo("v2"), "reloaded in memory");
        Assert.That(_cache.For("gbc")!.Version, Is.EqualTo("v2"));
        Assert.That((progress.Current, progress.Total), Is.EqualTo((10, 10)));
        var manifest = DatManifest.Read(Path.Combine(_cache.Directory, DatManifest.FileName));
        Assert.That(manifest, Has.Count.EqualTo(10));
        Assert.That(manifest.Single(e => e.File == $"no-intro/{Gb}.dat").Version, Is.EqualTo("v2"));
        Assert.That(manifest.Single(e => e.File == $"genre/{Gb}.dat").Version, Is.Null);
        Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(_paths.Cache, CrashRecovery.WorkFolder)), Is.Empty);
    }

    [Test]
    public async Task FirstRefresh_WithNoCache_Works()
    {
        await RunAsync(Server());

        Assert.That(_cache.For("gb")!.Version, Is.EqualTo("v2"));
    }

    [Test]
    public async Task OneFile404_FailsAndKeepsTheOldSet()
    {
        WriteLiveV1();
        File.Delete(Path.Combine(_remote, "genre", Gbc + ".dat"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(Server()));

        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        AssertStillV1();
    }

    [TestCase("no-intro", "not a dat (")]
    [TestCase("developer", "game ( comment \"x\" )")]
    [TestCase("no-intro", "clrmamepro ( name \"Nintendo - Virtual Boy\" version \"v2\" )")]
    public async Task BadFile_FailsNamingItAndKeepsTheOldSet(string kind, string body)
    {
        WriteLiveV1();
        File.WriteAllText(Path.Combine(_remote, kind, Gb + ".dat"), body);

        var ex = await Assert.ThrowsAsync<DatParseException>(() => RunAsync(Server()));

        Assert.That(ex!.Message, Does.StartWith($"{kind}/{Gb}.dat"));
        AssertStillV1();
    }

    [Test]
    public async Task Cancel_BetweenDownloads_KeepsTheOldSet()
    {
        WriteLiveV1();
        using var cts = new CancellationTokenSource();
        var count = 0;
        _override = _ =>
        {
            if (++count == 3)
                cts.Cancel();
            return null;
        };
        var server = Server();

        await Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => RunAsync(server, ct: cts.Token));

        Assert.That(server.Requests, Has.Count.EqualTo(3));
        AssertStillV1();
    }

    [Test]
    public async Task ThroughTheRunner_FailureEndsFailed()
    {
        WriteLiveV1();
        _override = _ => StubHttpHandler.Status(HttpStatusCode.NotFound);
        var jobs = Substitute.For<IJobRepository>();
        var runner = new JobRunner(jobs, new JobLock(), TimeProvider.System, NullLogger<JobRunner>.Instance);

        var started = await runner.StartAsync(Job(Server()), JobTrigger.Manual, null, CancellationToken.None);
        await started!.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        await jobs.Received(1).FinishAsync(started.Id, JobStatus.Failed, Arg.Any<DateTime>(), Arg.Is<string?>(x => x == null),
            Arg.Is<string>(e => e.Contains("404")), Arg.Any<CancellationToken>());
        AssertStillV1();
    }
}
