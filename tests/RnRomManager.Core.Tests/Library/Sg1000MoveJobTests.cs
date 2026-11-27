using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.Systems.Sega;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Library;

/// <summary>SG-1000 S12: the move job's outcomes, items, summary and cancellation, over real temp-dir ZIPs and the real mover.</summary>
public class Sg1000MoveJobTests
{
    private TempDir _dir = null!;
    private InfoJsonReader _reader = null!;
    private IGameRepository _games = null!;
    private IJobItemRepository _items = null!;
    private List<JobItem> _inserted = null!;
    private Sg1000MoveJob _job = null!;
    private int _seed;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        var systems = new SystemRegistry([new MasterSystemHandler(), new Sg1000Handler()]);
        _reader = new InfoJsonReader([], systems);
        var mirror = Substitute.For<ILibraryMirror>();
        var audit = Substitute.For<IAuditLog>();
        var paths = Options.Create(new PathsOptions { Roms = _dir.Path, Ingest = _dir.Combine("ingest"), Cache = _dir.Combine("cache") });
        var media = TestMedia.Offline();
        var retry = new GameMediaRetry(mirror, _reader, systems, media, paths, audit, TimeProvider.System, NullLogger<GameMediaRetry>.Instance);
        _games = Substitute.For<IGameRepository>();
        var editor = new LibraryEditor(new JobLock(), _games, Substitute.For<IRomRepository>(), Substitute.For<IImageRepository>(), mirror, _reader,
            new DatCache(paths, systems, NullLogger<DatCache>.Instance), systems, media, retry, paths, audit, new AuditPaths(paths),
            Substitute.For<ICollectionRepository>(), TimeProvider.System, NullLogger<LibraryEditor>.Instance);
        _inserted = [];
        _items = Substitute.For<IJobItemRepository>();
        _items.InsertAsync(Arg.Do<JobItem>(_inserted.Add), Arg.Any<CancellationToken>()).Returns(1L);
        _job = new Sg1000MoveJob(_games, editor, _items, TimeProvider.System, NullLogger<Sg1000MoveJob>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static GameRow Row(Guid id, string zipPath) => new()
    {
        Id = id, SystemId = "sms", Title = "T", DisplayTitle = "T", ZipPath = zipPath, Bucket = "T", Developers = [], Publishers = [], Genres = [],
        ZipSizeBytes = 1, ZipMtimeUtc = DateTime.UtcNow, InfoSchemaVersion = InfoJsonReader.CurrentVersion, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>A one-release Master System game dropped as <paramref name="original"/>.</summary>
    private async Task<GameRow> GameAsync(string title, string original)
    {
        var bytes = RomBytes(++_seed);
        var game = TestLibrary.Game(title, Rom($"{title}.sms", bytes) with { OriginalFileName = original }) with { System = "sms" };
        var zipPath = $"sms/{title[0]}/{title}.zip";
        Directory.CreateDirectory(_dir.Combine("sms", title[..1]));
        await new LibraryZipWriter(_reader).WriteAsync(_dir.Combine(zipPath), null,
            new LibraryZipChanges(game, new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry($"{title}.sms")] = () => new MemoryStream(bytes) }));
        return Row(game.GameId, zipPath);
    }

    private async Task<IReadOnlyList<GameRow>> FourAsync()
    {
        var broken = Row(Guid.NewGuid(), "sms/B/Broken.zip");
        Directory.CreateDirectory(_dir.Combine("sms", "B"));
        await File.WriteAllTextAsync(_dir.Combine("sms", "B", "Broken.zip"), "not a zip");
        IReadOnlyList<GameRow> rows = [await GameAsync("Alpha", "a.sg"), await GameAsync("Mixed", "m.sms"), broken, Row(Guid.NewGuid(), "sms/G/Gone.zip")];
        _games.ListByOriginalExtensionAsync("sms", ".sg", Arg.Any<CancellationToken>()).Returns(rows);
        return rows;
    }

    [Test]
    public async Task EachCandidate_GetsAnItem_AndTheSummaryCountsThem()
    {
        var rows = await FourAsync();

        var summary = JsonSerializer.Serialize(await _job.RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default));

        Assert.That(summary, Is.EqualTo("""{"moved":1,"skipped":2,"error":1}"""));
        Assert.That(_inserted.Select(i => (i.Outcome, i.Reason)), Is.EqualTo(new (string, string?)[]
        {
            ("Moved", null), ("Skipped", LibraryEditResult.NotSg1000), ("Error", LibraryTarget.TargetZipUnreadable), ("Skipped", LibraryEditResult.NotSg1000),
        }));
        Assert.That(_inserted.Select(i => (i.FileId, i.GameId, i.SystemId, i.SourcePath)),
            Is.EqualTo(rows.Select(r => (r.Id, (Guid?)r.Id, (string?)"sms", "roms/" + r.ZipPath))));
        Assert.That(File.Exists(_dir.Combine("sg1000", "A", "Alpha.zip")), Is.True);
    }

    [Test]
    public async Task ACancelledToken_StopsBetweenGames()
    {
        await FourAsync();
        using var cancel = new CancellationTokenSource();
        _items.When(i => i.InsertAsync(Arg.Any<JobItem>(), Arg.Any<CancellationToken>())).Do(_ => cancel.Cancel());

        await _job.RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), cancel.Token);

        Assert.That(_inserted.Select(i => i.Outcome), Is.EqualTo(new[] { "Moved" }));
    }
}
