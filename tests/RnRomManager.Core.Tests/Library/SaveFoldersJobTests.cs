using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Release editing B19: the one-off save folder migration over real temp-dir ZIPs.</summary>
public class SaveFoldersJobTests
{
    private TempDir _dir = null!;
    private InfoJsonReader _reader = null!;
    private ISystemRegistry _systems = null!;
    private ILibraryMirror _mirror = null!;
    private IJobItemRepository _items = null!;
    private IAuditLog _audit = null!;
    private ISettingsRepository _appSettings = null!;
    private List<GameRow> _rows = null!;
    private int _seed;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        var gb = Substitute.For<ISystemHandler>();
        gb.Id.Returns("gb");
        gb.EmbedRomsInZip.Returns(true);
        // A legacy system whose ROMs should be outside the ZIP: an embedded game is storage-pending.
        var nds = Substitute.For<ISystemHandler>();
        nds.Id.Returns("nds");
        nds.EmbedRomsInZip.Returns(false);
        nds.LegacyEmbeddedRoms.Returns(true);
        _systems = Substitute.For<ISystemRegistry>();
        _systems.All.Returns([gb, nds]);
        _systems.ById("gb").Returns(gb);
        _systems.ById("nds").Returns(nds);
        _reader = new InfoJsonReader([], _systems);
        _mirror = Substitute.For<ILibraryMirror>();
        _items = Substitute.For<IJobItemRepository>();
        _audit = Substitute.For<IAuditLog>();
        _appSettings = Substitute.For<ISettingsRepository>();
        _rows = [];
        _seed = 0;
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private SaveFoldersJob Job()
    {
        var games = Substitute.For<IGameRepository>();
        games.ListAllAsync(Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<GameRow>)_rows);
        var freeSpace = Substitute.For<IFreeSpace>();
        freeSpace.AvailableBytes(Arg.Any<string>()).Returns((long?)null);
        var settings = Substitute.For<IRomStorageSettings>();
        settings.Compression(Arg.Any<string>()).Returns(ZipCompression.Med);
        return new(games, _mirror, _reader, _systems, _items, Options.Create(new PathsOptions { Roms = _dir.Path }), _audit, freeSpace,
            TimeProvider.System, settings, _appSettings, NullLogger<SaveFoldersJob>.Instance);
    }

    private async Task<string> Run(CancellationToken ct = default) =>
        JsonSerializer.Serialize(await Job().RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), ct));

    private static GameSave Record(string? owner, byte[] bytes) => new()
    {
        Legacy = true, Id = GameSaves.NewId(), Name = "S", Description = "", Kind = GameSaves.KindBattery, Release = "x.gb", Extension = ".srm",
        SizeBytes = bytes.Length, Sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)), UploadedBy = owner, UploadedAt = When,
        UpdatedBy = owner, UpdatedAt = When,
    };

    /// <summary>A one-ROM game ZIP (mirrored as a row) with <paramref name="saves"/>: raw bytes for its index and entries.</summary>
    private async Task<GameRow> Add(string title, string system = "gb", byte[]? index = null, params (string Entry, byte[] Bytes)[] saves)
    {
        var bytes = RomBytes(++_seed);
        var game = Game(title, Rom(title + ".gb", bytes)) with { System = system };
        var zipPath = $"{system}/{title[0]}/{title}.zip";
        var path = _dir.Combine(system, title[..1], title + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await new LibraryZipWriter(_reader).WriteAsync(path, null, new LibraryZipChanges(game,
            new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry(game.Roms[0].FileName)] = () => new MemoryStream(bytes) }));
        if (index is not null)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
            foreach (var (name, content) in saves.Prepend((LibraryZip.SavesIndexEntry, index)))
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(content);
            }
        }
        var row = new MirrorEntry(game, zipPath, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)).ToRows().Game;
        _rows.Add(row);
        return row;
    }

    private string Abs(GameRow row) => _dir.Combine(row.ZipPath.Split('/'));

    private List<JobItem> Items() => _items.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IJobItemRepository.InsertAsync))
        .Select(c => (JobItem)c.GetArguments()[0]!).ToList();

    private List<AuditEvent> Audited() => _audit.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Record")
        .Select(c => (AuditEvent)c.GetArguments()[0]!).ToList();

    private SavesReadResult Read(GameRow row)
    {
        using var zip = LibraryZip.Open(Abs(row), _reader);
        return GameSaves.Read(zip);
    }

    [Test]
    public async Task EveryGame_MovedSkippedOrError_OwnersKeptOrAdmin_AuditedAndFlagged()
    {
        var alices = Record("Alice", [1, 2]);
        var unowned = Record(null, [3]);
        var v1 = await Add("Alpha", index: TestSaves.V1Index(alices, unowned), saves: [(alices.Entry, [1, 2]), (unowned.Entry, [3])]);
        var none = await Add("Bravo");
        var v2Record = Record("bob", [4]) with { Legacy = false };
        var v2 = await Add("Charlie", index: GameSaves.Serialize([v2Record]), saves: [(v2Record.Entry, [4])]);
        var broken = await Add("Delta", index: "{ no"u8.ToArray());
        var pendingRecord = Record("bob", [5]);
        var pending = await Add("Echo", "nds", TestSaves.V1Index(pendingRecord), (pendingRecord.Entry, [5]));
        var v2Before = File.ReadAllBytes(Abs(v2));

        Assert.That(await Run(), Is.EqualTo(JsonSerializer.Serialize(new { moved = 1, skipped = 2, error = 2 })));

        Assert.That(Items().Select(i => (i.GameId, i.Outcome, i.Reason)), Is.EqualTo(new (Guid?, string, string?)[]
        {
            (v1.Id, SaveFoldersJob.Moved, null), (none.Id, SaveFoldersJob.Skipped, null), (v2.Id, SaveFoldersJob.Skipped, null),
            (broken.Id, SaveFoldersJob.Error, LibraryEditResult.SavesUnreadable), (pending.Id, SaveFoldersJob.Error, LibraryEditResult.StorageMigrationPending),
        }));
        var moved = Read(v1);
        Assert.That(moved.State, Is.EqualTo(SavesState.Valid), moved.Problem);
        Assert.That(moved.Index!.Version, Is.EqualTo(2));
        Assert.That(moved.Index.Saves.Select(s => (s.UploadedBy, s.Entry)), Is.EqualTo(new[]
            { ((string?)"Alice", $"saves/alice/{alices.Id}.srm"), ("admin", $"saves/admin/{unowned.Id}.srm") }));
        using (var archive = ZipFile.OpenRead(Abs(v1)))
        {
            Assert.That(archive.Entries.Select(e => e.FullName).Where(n => n.StartsWith("saves/", StringComparison.Ordinal)).Order(),
                Is.EqualTo(new[] { $"saves/admin/{unowned.Id}.srm", $"saves/alice/{alices.Id}.srm", LibraryZip.SavesIndexEntry }.Order()));
        }
        Assert.That(File.ReadAllBytes(Abs(v2)), Is.EqualTo(v2Before), "a version 2 ZIP isn't touched");
        Assert.That(Read(pending).Index!.Version, Is.EqualTo(1), "a storage-pending game stays version 1");
        await _mirror.Received(1).ReplaceAsync(Arg.Is<MirrorEntry>(m => m.ZipPath == v1.ZipPath), Arg.Any<CancellationToken>());
        Assert.That(Audited().Select(a => (a.Action, a.GameId, a.Details)), Is.EqualTo(new[]
            { (AuditActions.SaveUpdated, (Guid?)v1.Id, (string?)"Moved saves to user folders") }));
        await _appSettings.Received(1).SetAsync(
            Arg.Is<IReadOnlyDictionary<string, string>>(d => d.Count == 1 && d[SaveFoldersJob.MigratedSetting] == "true"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cancelled_StopsBetweenGames_AndLeavesTheFlagUnset()
    {
        var record = Record("Alice", [1]);
        await Add("Alpha", index: TestSaves.V1Index(record), saves: (record.Entry, [1]));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.That(await Run(cancelled.Token), Is.EqualTo(JsonSerializer.Serialize(new { moved = 0, skipped = 0, error = 0 })));

        await _appSettings.DidNotReceiveWithAnyArgs().SetAsync(default!, default);
        Assert.That(Items(), Is.Empty);
    }
}
