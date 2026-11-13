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
public partial class LibraryMirrorTests
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
            handler.EmbedRomsInZip.Returns(true);
            handler.Id.Returns(id);
            return handler;
        }).Append(new ExternalTestHandler()).Append(new ExternalSetTestHandler()).ToArray();
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
            FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = InfoJsonImage.SourceDefault, SourceUrl = null, IsDefault = true, IsCover = true,
            SizeBytes = 99, Sha1 = new string('c', 40), Width = 512, Height = 512, DateAdded = When,
        };
        var game = Game("Tetris", rom) with { ReleaseYear = 1989, Developers = ["Nintendo"], Genres = ["Puzzle"], Images = [image], Rating = 3, Favourite = true };

        await _mirror.ReplaceAsync(Entry(game, "gb/T/Tetris.zip"), CancellationToken.None);

        var row = (await _games.GetAsync(game.GameId, CancellationToken.None))!;
        Assert.That(row with { Developers = [], Publishers = [], Genres = [] }, Is.EqualTo(new GameRow
        {
            Id = game.GameId, SystemId = "gb", Title = "Tetris", DisplayTitle = "Tetris", ZipPath = "gb/T/Tetris.zip", Bucket = "T",
            ReleaseYear = 1989, Developers = [], Publishers = [], Genres = [], Description = null, HasDefaultBoxArt = true, Rating = 3, Favourite = true,
            ZipSizeBytes = 1234, ZipMtimeUtc = Mtime, InfoSchemaVersion = InfoJsonReader.CurrentVersion, CreatedAt = When.UtcDateTime, UpdatedAt = When.UtcDateTime,
        }));
        Assert.That(row.Developers, Is.EqualTo(new[] { "Nintendo" }));
        Assert.That(row.Genres, Is.EqualTo(new[] { "Puzzle" }));
        var item = (await _games.ListAsync(new GameQuery(), CancellationToken.None)).Items.Single();
        Assert.That((item.Rating, item.Favourite), Is.EqualTo(((int?)3, true)));

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
            GameId = game.GameId, FileName = "boxart-01.png", Type = ImageType.BoxArt, Source = "default", SourceUrl = null, IsDefault = true, IsCover = true,
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
        await ZipAsync("md/S/Super.zip", "Super", 1);
        await ZipAsync("gb/T/Tetris.zip", "Tetris", 2);
        File.Copy(Abs("gb/T/Tetris.zip"), Abs("gb/T/Tetris.zip.tmp"));
        File.Copy(Abs("gb/T/Tetris.zip"), _dir.Combine("Loose.zip"));

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes.Select(c => c.ZipPath), Is.EqualTo(new[] { "gb/T/Tetris.zip" }));
    }

    // --- external ROMs (E4) ---

    private static InfoJsonGame ExternalGame(params (string FileName, string Pointer, int Seed)[] roms) =>
        Game("Orbit Test", [.. roms.Select((r, i) => Rom(r.FileName, RomBytes(r.Seed), isPrimary: i == 0) with { ExternalFileName = r.Pointer })])
            with { System = ExternalTestHandler.SystemId };

    [Test]
    public async Task Replace_WritesTheExternalPointer_NullForAnEmbeddedRom()
    {
        var external = ExternalGame(("Orbit Test [v0].nsp", "Orbit Test.Orbit Test [v0].nsp", 1), ("Orbit Test [v1].xci", "Orbit Test.Orbit Test [v1].xci", 2));
        var embedded = Game("Tetris", Rom("Tetris.gb", RomBytes(3)));

        await _mirror.ReplaceAsync(Entry(external, "ext/O/Orbit Test.zip"), CancellationToken.None);
        await _mirror.ReplaceAsync(Entry(embedded), CancellationToken.None);

        Assert.That((await _roms.ListByGameAsync(external.GameId, CancellationToken.None)).Select(r => (r.FileName, r.ExternalFileName)), Is.EqualTo(new[]
        {
            ("Orbit Test [v0].nsp", (string?)"Orbit Test.Orbit Test [v0].nsp"),
            ("Orbit Test [v1].xci", (string?)"Orbit Test.Orbit Test [v1].xci"),
        }));
        Assert.That((await _roms.ListByGameAsync(embedded.GameId, CancellationToken.None)).Single().ExternalFileName, Is.Null);
        Assert.That((await _roms.GetBySha1Async(external.Roms[1].Metadata.Sha1, CancellationToken.None))!.ExternalFileName, Is.EqualTo("Orbit Test.Orbit Test [v1].xci"));
    }

    [Test]
    public async Task Replace_WritesTheVersion_NullWhenUnset()
    {
        var game = ExternalGame(("Orbit Test [v0].nsp", "Orbit Test.Orbit Test [v0].nsp", 1), ("Orbit Test [v0][1.0.1].nsp", "Orbit Test.[v0][1.0.1].nsp", 2));
        game = game with { Roms = [game.Roms[0], game.Roms[1] with { Flags = ["Update"], Version = "1.0.1" }] };

        await _mirror.ReplaceAsync(Entry(game, "ext/O/Orbit Test.zip"), CancellationToken.None);

        var rows = await _roms.ListByGameAsync(game.GameId, CancellationToken.None);
        Assert.That(rows.Select(r => (r.FileName, r.Version)), Is.EqualTo(new[] { ("Orbit Test [v0].nsp", (string?)null), ("Orbit Test [v0][1.0.1].nsp", "1.0.1") }));
        Assert.That((await _roms.GetBySha1Async(game.Roms[1].Metadata.Sha1, CancellationToken.None))!.Version, Is.EqualTo("1.0.1"));
        Assert.That((await _roms.GetAsync(game.GameId, rows[1].Id, CancellationToken.None))!.Version, Is.EqualTo("1.0.1"));
    }

    [Test]
    public async Task Sync_ChangedZip_UpdatesTheVersion()
    {
        var game = await ZipAsync("gb/T/Tetris.zip", "Tetris", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        await WriteZipAsync(_reader, Abs("gb/T/Tetris.zip"), game with { Roms = [game.Roms[0] with { Version = "1.2" }] }, 1);

        await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That((await _roms.ListByGameAsync(game.GameId, CancellationToken.None)).Single().Version, Is.EqualTo("1.2"));
    }

    [Test]
    public async Task Sync_RebuildsTheExternalPointer_FromAZipWithoutARomEntry()
    {
        var game = ExternalGame(("Orbit Test [v0].nsp", "Orbit Test.Orbit Test [v0].nsp", 1));
        // Hand-made: only info.json, as an external game's ZIP holds no roms/ entry.
        var path = Abs("ext/O/Orbit Test.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var entry = archive.CreateEntry(LibraryZip.InfoJsonEntry).Open();
            entry.Write(InfoJsonSerializer.Serialize(game));
        }

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Skipped, Is.Empty);
        Assert.That((await _games.GetAsync(game.GameId, CancellationToken.None))!.ZipPath, Is.EqualTo("ext/O/Orbit Test.zip"));
        var rom = (await _roms.ListByGameAsync(game.GameId, CancellationToken.None)).Single();
        Assert.That((rom.SystemId, rom.ExternalFileName, rom.SizeBytes), Is.EqualTo(("ext", "Orbit Test.Orbit Test [v0].nsp", (long)RomBytes(1).Length)));
    }

    [Test]
    public async Task ListExternal_ReturnsOnlyExternalPointers_WithTheirOwner_EvenWhenTheFileIsAbsent()
    {
        // Nothing is on disk: the list is what the mirror claims, not what exists.
        var orbit = ExternalGame(("Orbit Test [v0].nsp", "Orbit Test.Orbit Test [v0].nsp", 1), ("Orbit Test [v1].xci", "Orbit Test.Orbit Test [v1].xci", 2));
        var comet = ExternalGame(("Comet [v0].nsp", "Comet.Comet [v0].nsp", 3)) with { Title = "Comet" };
        var embedded = Game("Tetris", Rom("Tetris.gb", RomBytes(4)));
        await _mirror.ReplaceAsync(Entry(orbit, "ext/O/Orbit Test.zip"), CancellationToken.None);
        await _mirror.ReplaceAsync(Entry(comet, "ext/C/Comet.zip"), CancellationToken.None);
        await _mirror.ReplaceAsync(Entry(embedded), CancellationToken.None);

        var rows = await _roms.ListExternalAsync(CancellationToken.None);

        Assert.That(rows, Is.EqualTo(new[]
        {
            new ExternalRomRow(comet.GameId, "ext/C/Comet.zip", "Comet.Comet [v0].nsp", RomBytes(3).Length, comet.Roms[0].Metadata.Sha1),
            new ExternalRomRow(orbit.GameId, "ext/O/Orbit Test.zip", "Orbit Test.Orbit Test [v0].nsp", RomBytes(1).Length, orbit.Roms[0].Metadata.Sha1),
            new ExternalRomRow(orbit.GameId, "ext/O/Orbit Test.zip", "Orbit Test.Orbit Test [v1].xci", RomBytes(2).Length, orbit.Roms[1].Metadata.Sha1),
        }));
    }

    [Test]
    public async Task ListExternal_WithOnlyEmbeddedRoms_IsEmpty()
    {
        await _mirror.ReplaceAsync(Entry(Game("Tetris", Rom("Tetris.gb", RomBytes(1)))), CancellationToken.None);

        Assert.That(await _roms.ListExternalAsync(CancellationToken.None), Is.Empty);
    }

    // --- checked traversal (G4) ---

    /// <summary>A folder outside <c>roms/</c> holding one valid ZIP that no sync may ever read.</summary>
    private async Task<(string Folder, InfoJsonGame Game)> OutsideAsync()
    {
        var folder = Path.Combine(Path.GetDirectoryName(_dir.Path)!, "rnrm-outside-" + Guid.NewGuid().ToString("N"));
        var game = Game("Outside", Rom("Outside.gb", RomBytes(90)));
        await WriteZipAsync(_reader, Path.Combine(folder, "Outside.zip"), game, 90);
        _cleanup.Add(folder);
        return (folder, game);
    }

    private readonly List<string> _cleanup = [];
    private readonly List<string> _links = [];

    [TearDown]
    public void RemoveLinksAndOutsideFolders()
    {
        foreach (var link in _links.Where(Directory.Exists))
            TestLinks.RemoveDirectoryLink(link);
        foreach (var folder in _cleanup.Where(Directory.Exists))
            Directory.Delete(folder, recursive: true);
        _links.Clear();
        _cleanup.Clear();
    }

    private void LinkDirectory(string relative, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Abs(relative))!);
        if (!TestLinks.TryLinkDirectory(Abs(relative), target))
            Assert.Ignore("can't create a directory link here");
        _links.Add(Abs(relative));
    }

    [Test]
    public async Task Sync_LinkedSubtree_IsNeverTraversed_AndReported()
    {
        var tetris = await ZipAsync("gb/T/Tetris.zip", "Tetris", 1);
        var (outside, hidden) = await OutsideAsync();
        LinkDirectory("gb/L", outside);

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.EqualTo(new[] { new DbSyncChangeItem(DbSyncChange.Inserted, tetris.GameId, "gb/T/Tetris.zip") }));
        Assert.That(result.Skipped.Select(s => (s.Reason, s.ZipPath)), Is.EqualTo(new[] { (DbSyncSkip.ZipUnreadable, "gb/L") }));
        Assert.That(await _games.GetAsync(hidden.GameId, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Sync_LinkedSystemFolder_IsNeverTraversed()
    {
        var (outside, hidden) = await OutsideAsync();
        LinkDirectory("gbc", outside);

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.Empty);
        Assert.That(result.Skipped.Select(s => (s.Reason, s.ZipPath)), Is.EqualTo(new[] { (DbSyncSkip.ZipUnreadable, "gbc") }));
        Assert.That(await _games.GetAsync(hidden.GameId, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Sync_LinkedZip_InAnEmbeddedFolder_IsNeverOpened()
    {
        var (outside, hidden) = await OutsideAsync();
        Directory.CreateDirectory(Abs("gb/O"));
        if (!TestLinks.TryLinkFile(Abs("gb/O/Outside.zip"), Path.Combine(outside, "Outside.zip")))
            Assert.Ignore("can't create a file link here");

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.Empty);
        Assert.That(result.Skipped.Select(s => (s.Reason, s.ZipPath)), Is.EqualTo(new[] { (DbSyncSkip.ZipUnreadable, "gb/O/Outside.zip") }));
        Assert.That(await _games.GetAsync(hidden.GameId, CancellationToken.None), Is.Null);
    }

    [Test]
    [Platform("Linux")]
    public async Task Sync_SpecialFileNamedZip_IsNeverOpened_AndOtherSpecialFilesAreIgnored()
    {
        var tetris = await ZipAsync("gb/T/Tetris.zip", "Tetris", 1);
        Assert.That(TestLinks.TryCreateFifo(Abs("gb/T/Pipe.zip")) && TestLinks.TryCreateFifo(Abs("gb/T/pipe.txt")), Is.True);

        // Opening the FIFO would block the sync forever.
        var result = await _sync.SyncAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

        Assert.That(result.Changes.Select(c => c.GameId), Is.EqualTo(new[] { tetris.GameId }));
        Assert.That(result.Skipped.Select(s => (s.Reason, s.ZipPath)), Is.EqualTo(new[] { (DbSyncSkip.ZipUnreadable, "gb/T/Pipe.zip") }));
    }

    [Test]
    public async Task Sync_SafeExternalZip_InAnotherRegisteredFolder_IsMirrored()
    {
        var game = ExternalGame(("Orbit Test [v0].nsp", "Orbit Test.Orbit Test [v0].nsp", 1));
        Directory.CreateDirectory(Abs("gb/O"));
        using (var archive = System.IO.Compression.ZipFile.Open(Abs("gb/O/Orbit Test.zip"), System.IO.Compression.ZipArchiveMode.Create))
        {
            using var entry = archive.CreateEntry(LibraryZip.InfoJsonEntry).Open();
            entry.Write(InfoJsonSerializer.Serialize(game));
        }

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.EqualTo(new[] { new DbSyncChangeItem(DbSyncChange.Inserted, game.GameId, "gb/O/Orbit Test.zip") }));
        var row = (await _games.GetAsync(game.GameId, CancellationToken.None))!;
        Assert.That((row.SystemId, row.ZipPath), Is.EqualTo(("ext", "gb/O/Orbit Test.zip")));
        Assert.That((await _roms.ListByGameAsync(game.GameId, CancellationToken.None)).Single().ExternalFileName, Is.EqualTo("Orbit Test.Orbit Test [v0].nsp"));
    }

    [Test]
    public async Task Sync_KnownZipsBehindAFolderThatBecameALink_KeepTheirRows_WhileAnAbsentZipIsStillPruned()
    {
        var legend = await ZipAsync("gb/L/Legend.zip", "Legend", 1);
        var lemmings = await ZipAsync("gb/L/deeper/Lemmings.zip", "Lemmings", 2);
        var tetris = await ZipAsync("gb/T/Tetris.zip", "Tetris", 3);
        await _sync.SyncAllAsync(CancellationToken.None);
        var (outside, _) = await OutsideAsync();
        Directory.Move(Abs("gb/L"), Path.Combine(outside, "moved-away"));
        LinkDirectory("gb/L", outside);
        File.Delete(Abs("gb/T/Tetris.zip"));

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.EqualTo(new[] { new DbSyncChangeItem(DbSyncChange.Deleted, tetris.GameId, "gb/T/Tetris.zip") }));
        Assert.That(result.Skipped.Select(s => (s.Reason, s.ZipPath)), Is.EquivalentTo(new[]
        {
            (DbSyncSkip.ZipUnreadable, "gb/L/Legend.zip"), (DbSyncSkip.ZipUnreadable, "gb/L/deeper/Lemmings.zip"),
        }));
        Assert.That(await _games.GetAsync(legend.GameId, CancellationToken.None), Is.Not.Null);
        Assert.That(await _games.GetAsync(lemmings.GameId, CancellationToken.None), Is.Not.Null);
        Assert.That(await _games.GetAsync(tetris.GameId, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Sync_KnownZipReplacedByALink_KeepsItsRow()
    {
        var tetris = await ZipAsync("gb/T/Tetris.zip", "Tetris", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        var (outside, _) = await OutsideAsync();
        File.Delete(Abs("gb/T/Tetris.zip"));
        if (!TestLinks.TryLinkFile(Abs("gb/T/Tetris.zip"), Path.Combine(outside, "Outside.zip")))
            Assert.Ignore("can't create a file link here");

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.Empty);
        Assert.That(result.Skipped.Select(s => (s.Reason, s.ZipPath)), Is.EqualTo(new[] { (DbSyncSkip.ZipUnreadable, "gb/T/Tetris.zip") }));
        Assert.That((await _games.GetAsync(tetris.GameId, CancellationToken.None))!.Title, Is.EqualTo("Tetris"));
    }

    [Test]
    public async Task Sync_CaseVariantsOfAKnownZip_KeepItsRow_AndNeitherIsRead()
    {
        var tetris = await ZipAsync("gb/T/Tetris.zip", "Tetris", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        var other = await ZipAsync("gb/T/tetris.zip", "Other", 2);
        if (Directory.GetFiles(Abs("gb/T")).Length != 2)
            Assert.Ignore("This volume is case-insensitive: it can't hold both names.");
        // Changed since the last sync, so only the ambiguity keeps it from being re-read.
        await WriteZipAsync(_reader, Abs("gb/T/Tetris.zip"), tetris with { Title = "Changed" }, 1);

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.Empty);
        var skip = result.Skipped.Single();
        Assert.That((skip.Reason, skip.ZipPath), Is.EqualTo((DbSyncSkip.ZipUnreadable, "gb/T/Tetris.zip")));
        Assert.That(skip.Details, Does.Contain("Tetris.zip, tetris.zip"));
        Assert.That((await _games.GetAsync(tetris.GameId, CancellationToken.None))!.Title, Is.EqualTo("Tetris"));
        Assert.That(await _games.GetAsync(other.GameId, CancellationToken.None), Is.Null);
    }

    [Test]
    [Platform("Linux")]
    public async Task Sync_BackslashInAZipName_IsNeverResolvedAsASeparator()
    {
        var known = await ZipAsync("gb/T/sub/Tetris.zip", "Tetris", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        File.Delete(Abs("gb/T/sub/Tetris.zip"));
        // One file named "sub\Tetris.zip" in gb/T: not the known ZIP's path.
        File.Copy(Abs("gb/T/sub/Other.zip", await ZipAsync("gb/T/sub/Other.zip", "Other", 2)), Path.Combine(Abs("gb/T"), "sub\\Tetris.zip"));
        File.Delete(Abs("gb/T/sub/Other.zip"));

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.EqualTo(new[] { new DbSyncChangeItem(DbSyncChange.Deleted, known.GameId, "gb/T/sub/Tetris.zip") }));
        Assert.That(result.Skipped.Select(s => (s.Reason, s.ZipPath)), Is.EqualTo(new[] { (DbSyncSkip.ZipUnreadable, "gb/T/sub\\Tetris.zip") }));
    }

    private string Abs(string relative, InfoJsonGame _) => Abs(relative);

    // --- paths the database treats as equal (E4) ---

    // "é" as one code point, and as "e" + a combining acute: two file names, one zip_path for the database.
    private const string ComposedZip = "ext/C/Caf\u00E9.zip";
    private const string DecomposedZip = "ext/C/Cafe\u0301.zip";

    /// <summary>An external game's ZIP: info.json only, as its ROM is a file next to it.</summary>
    private InfoJsonGame ExternalZip(string relative, string title, int seed)
    {
        var game = ExternalGame(($"{title} [v0].nsp", $"{title}.{title} [v0].nsp", seed)) with { Title = title, DisplayTitle = title };
        Directory.CreateDirectory(Path.GetDirectoryName(Abs(relative))!);
        using var archive = System.IO.Compression.ZipFile.Open(Abs(relative), System.IO.Compression.ZipArchiveMode.Create);
        using var entry = archive.CreateEntry(LibraryZip.InfoJsonEntry).Open();
        entry.Write(InfoJsonSerializer.Serialize(game));
        return game;
    }

    private void RequireBothSpellings()
    {
        if (Directory.GetFiles(Abs("ext/C")).Length != 2)
            Assert.Ignore("This volume normalizes names: it can't hold both spellings.");
    }

    [Test]
    public async Task Sync_TwoZipsTheDatabaseTreatsAsEqual_KeepTheExistingRow_AndAnUnrelatedZipIsStillRepaired()
    {
        var cafe = ExternalZip(ComposedZip, "Cafe One", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        var second = ExternalZip(DecomposedZip, "Cafe Two", 2);
        RequireBothSpellings();
        var orbit = ExternalZip("ext/O/Orbit.zip", "Orbit", 3);

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.EqualTo(new[] { new DbSyncChangeItem(DbSyncChange.Inserted, orbit.GameId, "ext/O/Orbit.zip") }));
        Assert.That(result.Skipped.Select(s => (s.Reason, s.ZipPath)), Is.EquivalentTo(new[]
        {
            (DbSyncSkip.ZipUnreadable, ComposedZip), (DbSyncSkip.ZipUnreadable, DecomposedZip),
        }));
        Assert.That(result.Skipped.Select(s => s.Details), Is.All.Contains(ComposedZip).And.All.Contains(DecomposedZip), "each spelling is named");
        var row = (await _games.GetAsync(cafe.GameId, CancellationToken.None))!;
        Assert.That((row.Title, row.ZipPath), Is.EqualTo(("Cafe One", ComposedZip)));
        Assert.That(await _games.GetAsync(second.GameId, CancellationToken.None), Is.Null);
        Assert.That((File.Exists(Abs(ComposedZip)), File.Exists(Abs(DecomposedZip))), Is.EqualTo((true, true)));

        // Still reported, and still nothing changed, on the next run.
        var again = await _sync.SyncAllAsync(CancellationToken.None);
        Assert.That((again.Changes.Count, again.Skipped.Count), Is.EqualTo((0, 2)));
    }

    [Test]
    public async Task Sync_TwoEqualZips_WithNoExistingRow_MirrorNeither_AndAnUnrelatedZipIsStillRepaired()
    {
        var first = ExternalZip(ComposedZip, "Cafe One", 1);
        var second = ExternalZip(DecomposedZip, "Cafe Two", 2);
        RequireBothSpellings();
        var orbit = ExternalZip("ext/O/Orbit.zip", "Orbit", 3);

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.EqualTo(new[] { new DbSyncChangeItem(DbSyncChange.Inserted, orbit.GameId, "ext/O/Orbit.zip") }));
        Assert.That(result.Skipped.Select(s => (s.Reason, s.ZipPath)), Is.EquivalentTo(new[]
        {
            (DbSyncSkip.ZipUnreadable, ComposedZip), (DbSyncSkip.ZipUnreadable, DecomposedZip),
        }));
        Assert.That(await _games.GetAsync(first.GameId, CancellationToken.None), Is.Null);
        Assert.That(await _games.GetAsync(second.GameId, CancellationToken.None), Is.Null);
        Assert.That((File.Exists(Abs(ComposedZip)), File.Exists(Abs(DecomposedZip))), Is.EqualTo((true, true)));
    }

    [Test]
    public async Task Sync_OldSpellingConfirmedAbsent_IsRelocatedToTheEqualNewSpelling()
    {
        var cafe = ExternalZip(ComposedZip, "Cafe One", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        File.Move(Abs(ComposedZip), Abs(DecomposedZip));

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.EqualTo(new[] { new DbSyncChangeItem(DbSyncChange.Updated, cafe.GameId, DecomposedZip) }));
        Assert.That(result.Skipped, Is.Empty);
        Assert.That((await _games.GetAsync(cafe.GameId, CancellationToken.None))!.ZipPath, Is.EqualTo(DecomposedZip));
    }

    [Test]
    public async Task Sync_OldSpellingConfirmedAbsent_IsReplacedByADifferentGameAtTheEqualSpelling()
    {
        var old = ExternalZip(ComposedZip, "Cafe One", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        File.Delete(Abs(ComposedZip));
        var replacement = ExternalZip(DecomposedZip, "Cafe Two", 2);

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Skipped, Is.Empty);
        Assert.That(await _games.GetAsync(old.GameId, CancellationToken.None), Is.Null);
        Assert.That((await _games.GetAsync(replacement.GameId, CancellationToken.None))!.ZipPath, Is.EqualTo(DecomposedZip));
    }

    [Test]
    public async Task Sync_OwnerOfAnEqualPathThatCouldNotBeChecked_IsPreserved_AndTheCandidateSkipped()
    {
        const string ownerPath = "ext/C/Sub\u00E9/Cafe.zip";
        const string candidatePath = "ext/C/Sube\u0301/Cafe.zip";
        var owner = ExternalZip(ownerPath, "Cafe One", 1);
        await _sync.SyncAllAsync(CancellationToken.None);
        // The owner's folder becomes a link: nobody can say whether its ZIP is still there.
        var (outside, _) = await OutsideAsync();
        Directory.Move(Abs("ext/C/Sub\u00E9"), Path.Combine(outside, "moved-away"));
        LinkDirectory("ext/C/Sub\u00E9", outside);
        // A real folder whose name the database can't tell from the linked one.
        var candidate = ExternalZip(candidatePath, "Cafe Two", 2);
        if (Directory.GetFileSystemEntries(Abs("ext/C")).Length != 2)
            Assert.Ignore("This volume normalizes names: it can't hold both spellings.");

        var result = await _sync.SyncAllAsync(CancellationToken.None);

        Assert.That(result.Changes, Is.Empty);
        Assert.That(result.Skipped.Select(s => (s.Reason, s.ZipPath)), Is.EquivalentTo(new[]
        {
            (DbSyncSkip.ZipUnreadable, ownerPath), (DbSyncSkip.ZipUnreadable, candidatePath),
        }));
        Assert.That(result.Skipped.Single(s => s.ZipPath == candidatePath).Details, Does.Contain(ownerPath).And.Contain("could not be checked"));
        Assert.That((await _games.GetAsync(owner.GameId, CancellationToken.None))!.ZipPath, Is.EqualTo(ownerPath));
        Assert.That(await _games.GetAsync(candidate.GameId, CancellationToken.None), Is.Null);
    }
}
