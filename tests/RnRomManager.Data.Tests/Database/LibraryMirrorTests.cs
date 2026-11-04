using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Spec §14.1/§14.2 mirror writes and §12.2 DB auto-repair (S22), against MariaDB + temp ZIPs.</summary>
[Category("Integration")]
public class LibraryMirrorTests
{
    private static readonly DateTime Mtime = new(2026, 11, 4, 12, 30, 15, DateTimeKind.Utc);

    private ILibraryMirror _mirror = null!;
    private GameRepository _games = null!;
    private RomRepository _roms = null!;
    private ImageRepository _images = null!;
    private InfoJsonReader _reader = null!;
    private TempDir _dir = null!;
    private DbSync _sync = null!;

    [SetUp]
    public async Task SetUp()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _mirror = new LibraryMirror(factory);
        _games = new GameRepository(factory);
        _roms = new RomRepository(factory);
        _images = new ImageRepository(factory);

        var handlers = new[] { "gb", "gbc" }.Select(id =>
        {
            var handler = Substitute.For<ISystemHandler>();
            handler.Id.Returns(id);
            return handler;
        }).ToArray();
        var systems = Substitute.For<ISystemRegistry>();
        systems.All.Returns(handlers);
        _reader = new InfoJsonReader([], systems);

        _dir = new TempDir();
        var paths = new PathsOptions { Roms = _dir.Path };
        _sync = new DbSync(_games, _roms, _mirror, _reader, systems, Options.Create(paths), NullLogger<DbSync>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static MirrorEntry Entry(InfoJsonGame game, string zipPath = "gb/T/Tetris.zip") => new(game, zipPath, 1234, Mtime.AddTicks(9999));

    private string Abs(string relative) => _dir.Combine(relative.Split('/'));

    private async Task<InfoJsonGame> ZipAsync(string relative, string title, params int[] seeds)
    {
        var game = Game(title, [.. seeds.Select((s, i) => Rom($"{title} {i}.gb", RomBytes(s), isPrimary: i == 0))]);
        await WriteZipAsync(_reader, Abs(relative), game, seeds);
        return game;
    }

    // --- mirror ---

    [Test]
    public async Task Replace_WritesEveryColumnFromInfoJson()
    {
        var rom = Rom("Tetris (USA, Europe).gb", RomBytes(1), regions: ["USA", "Europe"]) with
        {
            Flags = ["Proto"],
            Revision = "Rev 1",
            Identification = new InfoJsonIdentification(IdentificationMethod.Dat, "Nintendo - Game Boy", "2026.10.07", "Tetris (USA, Europe)"),
            Header = new JsonObject { ["title"] = "TETRIS" },
        };
        var image = new InfoJsonImage
        {
            FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = InfoJsonImage.SourceDefault, SourceUrl = null, IsDefault = true,
            SizeBytes = 99, Sha1 = new string('c', 40), Width = 512, Height = 512, DateAdded = When,
        };
        var game = Game("Tetris", rom) with { ReleaseYear = 1989, Developers = ["Nintendo"], Genres = ["Puzzle"], Images = [image] };

        await _mirror.ReplaceAsync(Entry(game, "gb/T/Tetris.zip"), CancellationToken.None);

        var row = (await _games.GetAsync(game.GameId, CancellationToken.None))!;
        Assert.That(row with { Developers = [], Publishers = [], Genres = [] }, Is.EqualTo(new GameRow
        {
            Id = game.GameId, SystemId = "gb", Title = "Tetris", DisplayTitle = "Tetris", ZipPath = "gb/T/Tetris.zip", Bucket = "T",
            ReleaseYear = 1989, Developers = [], Publishers = [], Genres = [], Description = null, HasDefaultBoxArt = true,
            ZipSizeBytes = 1234, ZipMtimeUtc = Mtime, InfoSchemaVersion = 1, CreatedAt = When.UtcDateTime, UpdatedAt = When.UtcDateTime,
        }));
        Assert.That(row.Developers, Is.EqualTo(new[] { "Nintendo" }));
        Assert.That(row.Genres, Is.EqualTo(new[] { "Puzzle" }));

        var romRow = (await _roms.ListByGameAsync(game.GameId, CancellationToken.None)).Single();
        Assert.That(romRow.RegionKey, Is.EqualTo("|USA|Europe|"));
        Assert.That(romRow.Regions, Is.EqualTo(new[] { "USA", "Europe" }));
        Assert.That(romRow.Flags, Is.EqualTo(new[] { "Proto" }));
        Assert.That(romRow.IdMethod, Is.EqualTo(IdentificationMethod.Dat));
        Assert.That(romRow.DatGameName, Is.EqualTo("Tetris (USA, Europe)"));
        Assert.That(romRow.IsPrimary, Is.True);
        Assert.That(romRow.Sha1, Is.EqualTo(rom.Metadata.Sha1));
        Assert.That(JsonNode.Parse(romRow.Header!)!["title"]!.GetValue<string>(), Is.EqualTo("TETRIS"));
        Assert.That(await _roms.GetBySha1Async(rom.Metadata.Sha1, CancellationToken.None), Is.Not.Null);

        var imageRow = (await _images.ListByGameAsync(game.GameId, CancellationToken.None)).Single();
        Assert.That(imageRow with { Id = 0 }, Is.EqualTo(new ImageRow
        {
            GameId = game.GameId, FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = "default", SourceUrl = null, IsDefault = true,
            SizeBytes = 99, Sha1 = new string('c', 40), Width = 512, Height = 512, DateAdded = When.UtcDateTime,
        }));
    }

    [Test]
    public async Task Replace_IsIdempotent_AndReplacesChildRows()
    {
        var game = Game("Tetris", Rom("a.gb", RomBytes(1)), Rom("b.gb", RomBytes(2), isPrimary: false));
        await _mirror.ReplaceAsync(Entry(game), CancellationToken.None);
        await _mirror.ReplaceAsync(Entry(game), CancellationToken.None);
        Assert.That(await _roms.ListByGameAsync(game.GameId, CancellationToken.None), Has.Count.EqualTo(2));

        var fewer = game with { Roms = [game.Roms[0]], Title = "Tetris DX" };
        await _mirror.ReplaceAsync(Entry(fewer, "gb/T/Tetris DX.zip"), CancellationToken.None);

        Assert.That(await _roms.ListByGameAsync(game.GameId, CancellationToken.None), Has.Count.EqualTo(1));
        Assert.That((await _games.GetAsync(game.GameId, CancellationToken.None))!.ZipPath, Is.EqualTo("gb/T/Tetris DX.zip"));
        Assert.That(await _games.ListSyncStateAsync(CancellationToken.None), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Replace_SameSha1InAnotherGame_ThrowsAndRollsBack()
    {
        var bytes = RomBytes(1);
        await _mirror.ReplaceAsync(Entry(Game("Tetris", Rom("a.gb", bytes))), CancellationToken.None);
        var other = Game("Other", Rom("b.gb", bytes));

        var ex = await Assert.ThrowsAsync<MySqlException>(() => _mirror.ReplaceAsync(Entry(other, "gb/O/Other.zip"), CancellationToken.None));

        Assert.That(ex!.ErrorCode, Is.EqualTo(MySqlErrorCode.DuplicateKeyEntry));
        Assert.That(await _games.GetAsync(other.GameId, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Replace_TakesOverAPathHeldByAnotherGame()
    {
        var old = Game("Tetris", Rom("a.gb", RomBytes(1)));
        await _mirror.ReplaceAsync(Entry(old), CancellationToken.None);
        var replacement = Game("Tetris", Rom("b.gb", RomBytes(2)));

        await _mirror.ReplaceAsync(Entry(replacement, "GB/t/tetris.zip"), CancellationToken.None); // zip_path is case-insensitive

        Assert.That(await _games.GetAsync(old.GameId, CancellationToken.None), Is.Null);
        Assert.That(await _games.GetAsync(replacement.GameId, CancellationToken.None), Is.Not.Null);
    }

    [Test]
    public async Task Delete_RemovesGameAndChildren()
    {
        var game = Game("Tetris", Rom("a.gb", RomBytes(1)));
        await _mirror.ReplaceAsync(Entry(game), CancellationToken.None);

        await _mirror.DeleteAsync(game.GameId, CancellationToken.None);

        Assert.That(await _games.GetAsync(game.GameId, CancellationToken.None), Is.Null);
        Assert.That(await _roms.ListByGameAsync(game.GameId, CancellationToken.None), Is.Empty);
    }

    // --- sync ---

    [Test]
    public async Task Sync_InsertsNewZips_ThenNothingWhenUnchanged()
    {
        var tetris = await ZipAsync("gb/T/Tetris.zip", "Tetris", 1);
        var zelda = await ZipAsync("gb/L/Legend.zip", "Legend", 2, 3);

        var first = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(first.Changes, Is.EquivalentTo(new[]
        {
            new DbSyncChangeItem(DbSyncChange.Inserted, tetris.GameId, "gb/T/Tetris.zip"),
            new DbSyncChangeItem(DbSyncChange.Inserted, zelda.GameId, "gb/L/Legend.zip"),
        }));
        Assert.That(await _roms.ListByGameAsync(zelda.GameId, CancellationToken.None), Has.Count.EqualTo(2));
        var state = (await _games.ListSyncStateAsync(CancellationToken.None)).Single(s => s.Id == tetris.GameId);
        Assert.That(state.ZipSizeBytes, Is.EqualTo(new FileInfo(Abs("gb/T/Tetris.zip")).Length));

        var second = await _sync.SyncAllAsync(CancellationToken.None);
        Assert.That(second.Changes, Is.Empty);
        Assert.That(second.Skipped, Is.Empty);
    }

    [Test]
    public async Task Sync_UpdatesChangedZip()
    {
        var game = await ZipAsync("gb/T/Tetris.zip", "Tetris", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        var changed = game with { Title = "Tetris DX", Roms = [.. game.Roms, Rom("Tetris (Japan).gb", RomBytes(5), isPrimary: false)] };
        await WriteZipAsync(_reader, Abs("gb/T/Tetris.zip"), changed, 1, 5);

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.EqualTo(new[] { new DbSyncChangeItem(DbSyncChange.Updated, game.GameId, "gb/T/Tetris.zip") }));
        Assert.That((await _games.GetAsync(game.GameId, CancellationToken.None))!.Title, Is.EqualTo("Tetris DX"));
        Assert.That(await _roms.ListByGameAsync(game.GameId, CancellationToken.None), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Sync_DeletesRowWhoseZipIsGone()
    {
        var game = await ZipAsync("gb/T/Tetris.zip", "Tetris", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        File.Delete(Abs("gb/T/Tetris.zip"));

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.EqualTo(new[] { new DbSyncChangeItem(DbSyncChange.Deleted, game.GameId, "gb/T/Tetris.zip") }));
        Assert.That(await _games.GetAsync(game.GameId, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Sync_MovedZip_UpdatesZipPathInsteadOfDeleting()
    {
        var game = await ZipAsync("gb/T/Tetris.zip", "Tetris", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        Directory.CreateDirectory(Abs("gbc/T"));
        File.Move(Abs("gb/T/Tetris.zip"), Abs("gbc/T/Tetris.zip"));

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.EqualTo(new[] { new DbSyncChangeItem(DbSyncChange.Updated, game.GameId, "gbc/T/Tetris.zip") }));
        Assert.That((await _games.GetAsync(game.GameId, CancellationToken.None))!.ZipPath, Is.EqualTo("gbc/T/Tetris.zip"));
    }

    [Test]
    public async Task Sync_SwappedPaths_BothUpdated()
    {
        var a = await ZipAsync("gb/A/A.zip", "A", 1);
        var b = await ZipAsync("gb/B/B.zip", "B", 2);
        await _sync.SyncAllAsync(CancellationToken.None);
        File.Move(Abs("gb/A/A.zip"), Abs("gb/A/tmp.bin"));
        File.Move(Abs("gb/B/B.zip"), Abs("gb/A/A.zip"));
        File.Move(Abs("gb/A/tmp.bin"), Abs("gb/B/B.zip"));
        File.SetLastWriteTimeUtc(Abs("gb/A/A.zip"), Mtime);
        File.SetLastWriteTimeUtc(Abs("gb/B/B.zip"), Mtime);

        await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That((await _games.GetAsync(a.GameId, CancellationToken.None))!.ZipPath, Is.EqualTo("gb/B/B.zip"));
        Assert.That((await _games.GetAsync(b.GameId, CancellationToken.None))!.ZipPath, Is.EqualTo("gb/A/A.zip"));
    }

    [Test]
    public async Task Sync_UnreadableZip_KeepsItsRow()
    {
        var game = await ZipAsync("gb/T/Tetris.zip", "Tetris", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        await File.WriteAllBytesAsync(Abs("gb/T/Tetris.zip"), [1, 2, 3]);

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.Empty);
        Assert.That(result.Skipped.Single().Reason, Is.EqualTo(DbSyncSkip.ZipUnreadable));
        Assert.That(await _games.GetAsync(game.GameId, CancellationToken.None), Is.Not.Null);
    }

    [Test]
    public async Task Sync_InvalidAndNewerAndMissingInfoJson_AreSkipped()
    {
        var invalid = Game("Bad", Rom("bad.gb", RomBytes(1), isPrimary: false));
        Directory.CreateDirectory(Abs("gb/B"));
        using (var zip = System.IO.Compression.ZipFile.Open(Abs("gb/B/Bad.zip"), System.IO.Compression.ZipArchiveMode.Create))
            await using (var s = zip.CreateEntry("info.json").Open())
                await s.WriteAsync(InfoJsonSerializer.Serialize(invalid));
        var newer = JsonNode.Parse(InfoJsonSerializer.Serialize(Game("New", Rom("new.gb", RomBytes(2)))))!;
        newer["schemaVersion"] = 99;
        Directory.CreateDirectory(Abs("gb/N"));
        using (var zip = System.IO.Compression.ZipFile.Open(Abs("gb/N/New.zip"), System.IO.Compression.ZipArchiveMode.Create))
            await using (var s = zip.CreateEntry("info.json").Open())
                await s.WriteAsync(System.Text.Encoding.UTF8.GetBytes(newer.ToJsonString()));
        using (var zip = System.IO.Compression.ZipFile.Open(Abs("gb/N/Empty.zip"), System.IO.Compression.ZipArchiveMode.Create))
            zip.CreateEntry("roms/x.gb");

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.Empty);
        Assert.That(result.Skipped.Select(s => (s.Reason, s.ZipPath)), Is.EquivalentTo(new[]
        {
            (DbSyncSkip.InfoJsonInvalid, "gb/B/Bad.zip"),
            (DbSyncSkip.InfoJsonNewerSchema, "gb/N/New.zip"),
            (DbSyncSkip.InfoJsonMissing, "gb/N/Empty.zip"),
        }));
    }

    [Test]
    public async Task Sync_DuplicateGameId_SkipsBoth_KeepsExistingRow()
    {
        var game = await ZipAsync("gb/T/Tetris.zip", "Tetris", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        // A crash during a rename leaves the same gameId at the new path too (§12.2).
        await WriteZipAsync(_reader, Abs("gb/T/Tetris DX.zip"), game with { Title = "Tetris DX" }, 1);
        File.SetLastWriteTimeUtc(Abs("gb/T/Tetris.zip"), Mtime);

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.Empty);
        Assert.That(result.Skipped.Select(s => s.Reason), Is.EqualTo(new[] { DbSyncSkip.DuplicateGameId, DbSyncSkip.DuplicateGameId }));
        Assert.That(result.Skipped[0].Details, Does.Contain("gb/T/Tetris.zip").And.Contain("gb/T/Tetris DX.zip"));
        Assert.That((await _games.GetAsync(game.GameId, CancellationToken.None))!.Title, Is.EqualTo("Tetris"));
    }

    [Test]
    public async Task Sync_DuplicateSha1_SkipsTheLaterZip()
    {
        var first = await ZipAsync("gb/A/Alpha.zip", "Alpha", 1);
        var later = await ZipAsync("gb/B/Beta.zip", "Beta", 1);

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.EqualTo(new[] { new DbSyncChangeItem(DbSyncChange.Inserted, first.GameId, "gb/A/Alpha.zip") }));
        Assert.That(result.Skipped.Single(), Is.EqualTo(result.Skipped.Single() with { Reason = DbSyncSkip.DuplicateSha1, ZipPath = "gb/B/Beta.zip" }));
        Assert.That(await _games.GetAsync(later.GameId, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Sync_IgnoresUnregisteredFoldersAndNonZips()
    {
        await ZipAsync("snes/S/Super.zip", "Super", 1);
        await ZipAsync("gb/T/Tetris.zip", "Tetris", 2);
        File.Copy(Abs("gb/T/Tetris.zip"), Abs("gb/T/Tetris.zip.tmp"));
        File.Copy(Abs("gb/T/Tetris.zip"), _dir.Combine("Loose.zip"));

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes.Select(c => c.ZipPath), Is.EqualTo(new[] { "gb/T/Tetris.zip" }));
    }
}
