using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Dats;

/// <summary>Spec T1, T2: the titledb part of a refresh, one shared download for every TitleDb system.</summary>
public partial class DatRefreshJobTests
{
    private const string TitleDbUrl = "https://titledb.test/US.en.json";
    private const string SwitchA = "Test - Switch A";
    private const string SwitchB = "Test - Switch B";

    private static readonly string Upstream = """
        {
          "1": { "id": "0100000000001000", "name": "Orbit Expedition", "releaseDate": 20200101, "size": 90958725120 },
          "2": { "id": "0100000000002000", "name": "The Second Game", "languages": ["en"] }
        }
        """;

    private static ISystemHandler TitleDbHandler(string id, string datName)
    {
        var handler = Handler(id, datName);
        handler.DatSource.Returns(DatSource.TitleDb);
        return handler;
    }

    /// <summary>gb and gbc (libretro) plus two TitleDb systems.</summary>
    private void UseTitleDb(string secondDatName = SwitchB)
    {
        ISystemHandler[] handlers = [Handler("gb", Gb), Handler("gbc", Gbc), TitleDbHandler("swa", SwitchA), TitleDbHandler("swb", secondDatName)];
        _systems.All.Returns(handlers);
    }

    private bool IsTitleDb(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri == TitleDbUrl;

    /// <summary>Answers the titledb request with <paramref name="respond"/>; everything else from the libretro "server".</summary>
    private void ServeTitleDb(Func<HttpResponseMessage> respond) => _override = request => IsTitleDb(request) ? respond() : null;

    private static HttpResponseMessage Json(string body) => StubHttpHandler.Bytes(Encoding.UTF8.GetBytes(body));

    private DatRefreshJob TitleDbJob(StubHttpHandler server, TimeProvider? time = null, DatPublicationHooks? hooks = null) =>
        new(new HttpClient(server), new SourcesOptions { DatBaseUrl = BaseUrl + "/", TitleDbUrl = TitleDbUrl }, _paths, _cache, _systems, _audit,
            time ?? TimeProvider.System, NullLogger<DatRefreshJob>.Instance) { PublicationHooks = hooks };

    private Task<object?> RunTitleDbAsync(StubHttpHandler server, JobProgress? progress = null, CancellationToken ct = default, TimeProvider? time = null,
        DatPublicationHooks? hooks = null, Guid? id = null) =>
        TitleDbJob(server, time, hooks).RunAsync(new JobContext(id ?? Guid.NewGuid(), progress ?? new JobProgress()), ct);

    private static int Files(object? result) => (int)result!.GetType().GetProperty("files")!.GetValue(result)!;

    [Test]
    public async Task TwoTitleDbSystems_ShareOneDownload_AndEachGetsAnEntry()
    {
        WriteLiveV1();
        UseTitleDb();
        ServeTitleDb(() => Json(Upstream));
        var server = Server();
        var progress = new JobProgress();
        var id = Guid.NewGuid();
        var source = Path.Combine(_paths.Cache, CrashRecovery.WorkFolder, id.ToString(), DatRefreshJob.TitleDbSourceFile);
        var sourceSeen = false;
        var hooks = new DatPublicationHooks { Move = (from, to) => { sourceSeen |= File.Exists(source); Directory.Move(from, to); } };

        var result = await RunTitleDbAsync(server, progress, hooks: hooks, id: id);

        Assert.That(server.Requests.Count(IsTitleDb), Is.EqualTo(1));
        Assert.That(server.Requests, Has.Count.EqualTo(11), "5 per libretro system, one titledb");
        Assert.That((progress.Current, progress.Total), Is.EqualTo((11, 11)), "5L + 1");
        Assert.That(Files(result), Is.EqualTo(12), "5L + 2");
        Assert.That(sourceSeen, Is.True, "the body went to the job's work folder");
        Assert.That(File.Exists(source), Is.False, "and is gone after the job");

        var manifest = DatManifest.Read(Path.Combine(_cache.Directory, DatManifest.FileName));
        Assert.That(manifest.Where(e => e.File.StartsWith("titledb/")).Select(e => (e.File, e.DatName)), Is.EquivalentTo(new[]
        {
            (TitleDb.RelativePath(SwitchA), SwitchA),
            (TitleDb.RelativePath(SwitchB), SwitchB),
        }));
        Assert.That(_cache.For("swa")!.ByTitleId("0100000000001000")!.ReleaseYear, Is.EqualTo(2020));
        Assert.That(_cache.For("swb")!.Roms, Has.Count.EqualTo(2));
        Assert.That(_cache.For("gb")!.Version, Is.EqualTo("v2"));
    }

    [Test]
    public async Task LibretroOnlyPlan_MakesNoTitleDbRequest()
    {
        var server = Server();

        var result = await RunTitleDbAsync(server);

        Assert.That(server.Requests.Any(IsTitleDb), Is.False);
        Assert.That(Files(result), Is.EqualTo(10));
    }

    [Test]
    public void TheCapIs256MiB() => Assert.That(DatRefreshJob.MaxTitleDbBytes, Is.EqualTo(256L * 1024 * 1024));

    [TestCase(true, TestName = "OversizedSource_FailsAndKeepsTheOldSet with Content-Length")]
    [TestCase(false, TestName = "OversizedSource_FailsAndKeepsTheOldSet without Content-Length")]
    public async Task OversizedSource_FailsAndKeepsTheOldSet(bool contentLength)
    {
        WriteLiveV1();
        UseTitleDb();
        var body = Encoding.UTF8.GetBytes(Upstream);
        ServeTitleDb(() => contentLength ? Json(Upstream) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Chunks(body)) });
        var job = TitleDbJob(Server());
        job.TitleDbByteLimit = body.Length - 1;

        await Assert.ThrowsAsync<LimitExceededException>(() => job.RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default));

        AssertStillV1();
    }

    [Test]
    public async Task SourceAtTheCap_WithoutContentLength_IsAccepted()
    {
        UseTitleDb();
        var body = Encoding.UTF8.GetBytes(Upstream);
        ServeTitleDb(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Chunks(body)) });
        var job = TitleDbJob(Server());
        job.TitleDbByteLimit = body.Length;

        await job.RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);

        Assert.That(_cache.For("swa"), Is.Not.Null);
    }

    [TestCase("404", TestName = "BadSource_FailsAndKeepsTheOldSet failed download")]
    [TestCase("[]", TestName = "BadSource_FailsAndKeepsTheOldSet array")]
    [TestCase("null", TestName = "BadSource_FailsAndKeepsTheOldSet JSON null")]
    [TestCase("{ not json", TestName = "BadSource_FailsAndKeepsTheOldSet malformed")]
    public async Task BadSource_FailsAndKeepsTheOldSet(string body)
    {
        WriteLiveV1();
        UseTitleDb();
        ServeTitleDb(() => body == "404" ? StubHttpHandler.Status(HttpStatusCode.NotFound) : Json(body));

        await Assert.CatchAsync(() => RunTitleDbAsync(Server()));

        AssertStillV1();
        _audit.DidNotReceiveWithAnyArgs().Record(default!);
    }

    [Test]
    public async Task CancelAfterTheStagedManifest_KeepsTheOldSet()
    {
        WriteLiveV1();
        UseTitleDb();
        ServeTitleDb(() => Json(Upstream));
        using var cts = new CancellationTokenSource();
        _cache.AfterCompactRead = _ => cts.Cancel(); // during the staged validation, after dats.json was written

        await Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => RunTitleDbAsync(Server(), ct: cts.Token));

        _cache.AfterCompactRead = null;
        AssertStillV1();
    }

    [Test]
    public async Task CancelDuringTheBodyRead_KeepsTheOldSet()
    {
        WriteLiveV1();
        UseTitleDb();
        using var cts = new CancellationTokenSource();
        ServeTitleDb(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new Chunks(Encoding.UTF8.GetBytes(Upstream), afterFirstChunk: cts.Cancel)),
        });

        await Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => RunTitleDbAsync(Server(), ct: cts.Token));

        AssertStillV1();
    }

    [Test]
    public async Task OneInvariantUtcDate_ForEveryCompactEntry()
    {
        UseTitleDb();
        ServeTitleDb(() => Json(Upstream));
        // Every reading is a second later, starting just before midnight UTC: the libretro files straddle it.
        var clock = new SteppingClock(new DateTimeOffset(2026, 11, 10, 23, 59, 55, TimeSpan.Zero));
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try
        {
            await RunTitleDbAsync(Server(), time: clock);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        var entries = DatManifest.Read(Path.Combine(_cache.Directory, DatManifest.FileName)).Where(e => e.File.StartsWith("titledb/")).ToList();
        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries.Select(e => (e.Version, e.DownloadedAt)).Distinct().Count(), Is.EqualTo(1), "one sample for both");
        Assert.That(entries[0].Version, Is.EqualTo(entries[0].DownloadedAt.UtcDateTime.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)));
        Assert.That(entries[0].Version, Is.EqualTo("2026.11.11"), "Gregorian and UTC, sampled after the download");
    }

    [Test]
    public async Task CaseFoldedDuplicateDatNames_FailBeforeAnything()
    {
        WriteLiveV1();
        UseTitleDb(secondDatName: SwitchA.ToUpperInvariant());
        ServeTitleDb(() => Json(Upstream));
        var server = Server();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunTitleDbAsync(server));

        Assert.That(ex!.Message, Does.Contain("'swa'").And.Contain("'swb'"));
        Assert.That(server.Requests, Is.Empty);
        Assert.That(Directory.Exists(Path.Combine(_paths.Cache, CrashRecovery.WorkFolder)), Is.False, "no folder made");
        _audit.DidNotReceiveWithAnyArgs().Record(default!);
        AssertStillV1();
    }

    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private long _readings;

        public override DateTimeOffset GetUtcNow() => start.AddSeconds(Interlocked.Increment(ref _readings));
    }

    /// <summary>A body without a length, served in two chunks; honours the read's token.</summary>
    private sealed class Chunks(byte[] data, Action? afterFirstChunk = null) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var half = Math.Max(1, data.Length / 2);
            var n = Math.Min(buffer.Length, Math.Min(data.Length - _position, _position == 0 ? half : data.Length));
            data.AsSpan(_position, n).CopyTo(buffer);
            var first = _position == 0;
            _position += n;
            if (first)
                afterFirstChunk?.Invoke();
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
