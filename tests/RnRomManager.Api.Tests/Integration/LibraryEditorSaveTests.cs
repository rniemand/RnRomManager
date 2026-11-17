using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
using static RnRomManager.Core.Library.LibraryEditResult;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>B8–B13: <c>LibraryEditor</c>'s save operations, run from the host's services against MariaDB and real ZIPs.</summary>
[Category("Integration")]
public class LibraryEditorSaveTests
{
    private static readonly AuditActor Alice = new(null, "alice", null);
    private static readonly SaveViewer AliceViewer = new("alice", false);

    private ApiFactory _factory = null!;
    private int _seed;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();
    private LibraryEditor Editor => Service<LibraryEditor>();

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => SystemHandlerRegistration.AddSystemHandler<LegacyExternalTestHandler>(services),
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public Task SetUp() => _factory.ResetAsync();

    private string Roms => Path.Combine(_factory.Root, "roms");

    private sealed record TestGame(Guid Id, string ZipPath, string Zip, long[] RomIds, string[] FileNames);

    /// <summary>A mirrored Game Boy game with one release per name, all inside its ZIP.</summary>
    private async Task<TestGame> WriteGame(string title, params string[] fileNames)
    {
        var seeds = fileNames.Select(_ => ++_seed).ToArray();
        var game = TestLibrary.Game(title, [.. fileNames.Select((f, i) => TestLibrary.Rom(f, TestLibrary.RomBytes(seeds[i]), isPrimary: i == 0))]);
        var zipPath = $"gb/{title[..1]}/{title}.zip";
        var zip = Path.Combine(Roms, zipPath);
        await TestLibrary.WriteZipAsync(Service<InfoJsonReader>(), zip, game, seeds);
        var file = new FileInfo(zip);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, zipPath, file.Length, file.LastWriteTimeUtc), default);
        var rows = await Service<IRomRepository>().ListByGameAsync(game.GameId, default);
        return new(game.GameId, zipPath, zip, [.. fileNames.Select(f => rows.Single(r => r.FileName == f).Id)], fileNames);
    }

    private async Task<long> RomId(Guid gameId, string fileName) =>
        (await Service<IRomRepository>().ListByGameAsync(gameId, default)).Single(r => r.FileName == fileName).Id;

    private SaveUpload Upload(byte[] bytes, string extension = ".state")
    {
        var path = Path.Combine(_factory.Root, $"upload-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(path, bytes);
        return new(path, extension, bytes.Length, Convert.ToHexStringLower(SHA1.HashData(bytes)));
    }

    private static GameSave Record(string release, int descriptionLength = 0) => new()
    {
        Id = GameSaves.NewId(), Name = "Old", Description = new string('d', descriptionLength), Kind = GameSaves.KindBattery, Release = release,
        Extension = ".srm", SizeBytes = 1, Sha1 = Convert.ToHexStringLower(SHA1.HashData(new byte[] { 7 })), UploadedBy = null,
        UploadedAt = TestLibrary.When, UpdatedBy = null, UpdatedAt = TestLibrary.When,
    };

    /// <summary>Puts a <c>saves/</c> folder into the ZIP by hand (an index of <paramref name="saves"/> with one-byte entries), or raw entries.</summary>
    private static void AddSaves(string zip, IReadOnlyList<GameSave>? saves, params (string Name, byte[] Bytes)[] raw)
    {
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
        var entries = raw.ToList();
        if (saves is not null)
            entries.AddRange([(LibraryZip.SavesIndexEntry, GameSaves.Serialize(saves)!), .. saves.Select(s => (s.Entry, new byte[] { 7 }))]);
        foreach (var (name, bytes) in entries)
        {
            using var entry = archive.CreateEntry(name).Open();
            entry.Write(bytes);
        }
    }

    private SavesReadResult ReadSaves(string zip)
    {
        using var library = LibraryZip.Open(zip, Service<InfoJsonReader>());
        return GameSaves.Read(library);
    }

    private static byte[] Entry(string zip, string name)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var stream = archive.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string[] SavesEntries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return [.. archive.Entries.Select(e => e.FullName).Where(n => n.StartsWith(LibraryZip.SavesFolder, StringComparison.Ordinal)).Order()];
    }

    private async Task<List<(string Action, string Path, string Details)>> AuditRows()
    {
        await Service<AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        return [.. await db.QueryAsync<(string, string, string)>("SELECT action, path, details FROM audit_events WHERE action LIKE 'Save%' ORDER BY id")];
    }

    [Test]
    public async Task Add_Update_Replace_Delete_WriteTheZip_AndAuditEachChange()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");

        var added = await Editor.AddSaveAsync(game.Id, game.RomIds[0], "state", "  Before boss  ", null, Upload([1, 2, 3]), Alice, default);
        Assert.That(added.Code, Is.Null, added.Details);
        var save = added.Save!;
        Assert.That((save.Name, save.Description, save.Release, save.Extension, save.SizeBytes, save.UploadedBy),
            Is.EqualTo(("Before boss", "", "Tetris (World).gb", ".state", 3L, (string?)"alice")));
        Assert.That(Entry(game.Zip, save.Entry), Is.EqualTo(new byte[] { 1, 2, 3 }));

        var updated = await Editor.UpdateSaveAsync(game.Id, save.Id, "After boss", "line 1\nline 2", Alice, AliceViewer, default);
        Assert.That((updated.Code, updated.Save!.Name, updated.Save.Description), Is.EqualTo(((string?)null, "After boss", "line 1\nline 2")));

        var replaced = await Editor.ReplaceSaveFileAsync(game.Id, save.Id, Upload([9, 9], ".srm"), Alice, AliceViewer, default);
        Assert.That(replaced.Code, Is.Null, replaced.Details);
        Assert.That(SavesEntries(game.Zip), Is.EqualTo(new[] { LibraryZip.SaveEntry(save.Id + ".srm"), LibraryZip.SavesIndexEntry }));
        Assert.That(Entry(game.Zip, replaced.Save!.Entry), Is.EqualTo(new byte[] { 9, 9 }));
        Assert.That((replaced.Save.Name, replaced.Save.SizeBytes), Is.EqualTo(("After boss", 2L)));

        Assert.That((await Editor.DeleteSaveAsync(game.Id, save.Id, Alice, AliceViewer, default)).Code, Is.Null);
        Assert.That(SavesEntries(game.Zip), Is.Empty, "the last delete removes saves.json too");

        var details = $"state \"{{0}}\" ({save.Id})";
        Assert.That(await AuditRows(), Is.EqualTo(new[]
        {
            (AuditActions.SaveAdded, "roms/gb/T/Tetris.zip", string.Format(details, "Before boss")),
            (AuditActions.SaveUpdated, "roms/gb/T/Tetris.zip", string.Format(details, "After boss")),
            (AuditActions.SaveUpdated, "roms/gb/T/Tetris.zip", string.Format(details, "After boss")),
            (AuditActions.SaveDeleted, "roms/gb/T/Tetris.zip", string.Format(details, "After boss")),
        }));
    }

    [Test]
    public async Task Update_ThatChangesNothing_WritesNothing_AndAuditsNothing()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var save = (await Editor.AddSaveAsync(game.Id, game.RomIds[0], "state", "Same", "Text", Upload([1]), Alice, default)).Save!;
        var before = File.ReadAllBytes(game.Zip);

        var result = await Editor.UpdateSaveAsync(game.Id, save.Id, " Same ", "Text", Alice, AliceViewer, default);

        Assert.That((result.Code, result.Save), Is.EqualTo(((string?)null, save)));
        Assert.That(File.ReadAllBytes(game.Zip), Is.EqualTo(before));
        Assert.That((await AuditRows()).Select(r => r.Action), Is.EqualTo(new[] { AuditActions.SaveAdded }));
    }

    [Test]
    public async Task EveryOperation_WhileAJobRuns_IsJobRunning()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var save = (await Editor.AddSaveAsync(game.Id, game.RomIds[0], "state", "S", null, Upload([1]), Alice, default)).Save!;
        using var held = Service<JobLock>().TryAcquire();

        Assert.That(new[]
        {
            (await Editor.AddSaveAsync(game.Id, game.RomIds[0], "state", "S", null, Upload([1]), Alice, default)).Code,
            (await Editor.UpdateSaveAsync(game.Id, save.Id, "T", null, Alice, AliceViewer, default)).Code,
            (await Editor.ReplaceSaveFileAsync(game.Id, save.Id, Upload([2]), Alice, AliceViewer, default)).Code,
            (await Editor.DeleteSaveAsync(game.Id, save.Id, Alice, AliceViewer, default)).Code,
        }, Is.All.EqualTo(JobRunning));
    }

    [Test]
    public async Task UnknownGameReleaseOrSave_IsNotFound()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var other = await WriteGame("Pinball", "Pinball (World).gb");

        Assert.That(new[]
        {
            (await Editor.AddSaveAsync(Guid.NewGuid(), game.RomIds[0], "state", "S", null, Upload([1]), Alice, default)).Code,
            (await Editor.AddSaveAsync(game.Id, other.RomIds[0], "state", "S", null, Upload([1]), Alice, default)).Code,
            (await Editor.UpdateSaveAsync(game.Id, GameSaves.NewId(), "T", null, Alice, AliceViewer, default)).Code,
            (await Editor.DeleteSaveAsync(game.Id, GameSaves.NewId(), Alice, AliceViewer, default)).Code,
        }, Is.All.EqualTo(NotFound));
    }

    [Test]
    public async Task Add_25SavesNamingTheRelease_InAnyCase_IsSaveLimitReached_AnOrphanCountsForNone()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        AddSaves(game.Zip, [.. Enumerable.Range(0, 25).Select(_ => Record("TETRIS (WORLD).GB") with { UploadedBy = "ALICE" }), Record("Gone (World).gb")]);

        var full = await Editor.AddSaveAsync(game.Id, game.RomIds[0], "state", "S", null, Upload([1]), Alice, default);

        Assert.That((full.Code, full.Details), Is.EqualTo((SaveLimitReached, SaveLimitReachedTitle)));
    }

    [Test]
    public async Task Add_ThatWouldGrowTheIndexOverOneMiB_IsSavesIndexFull()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        // Same-sized orphan records until the index would pass 1 MiB; one fewer is on disk.
        var saves = new List<GameSave>();
        while (GameSaves.Serialize(saves) is not null)
            saves.Add(Record("Gone (World).gb", GameSaves.MaxDescriptionLength) with { Name = "SSSS" });
        saves.RemoveAt(saves.Count - 1);
        AddSaves(game.Zip, saves);
        var before = File.ReadAllBytes(game.Zip);

        var result = await Editor.AddSaveAsync(game.Id, game.RomIds[0], "battery", "SSSS", new string('d', GameSaves.MaxDescriptionLength),
            Upload([7], ".srm"), Alice, default);

        Assert.That((result.Code, result.Details), Is.EqualTo((SavesIndexFull, SavesIndexFullTitle)));
        Assert.That(File.ReadAllBytes(game.Zip), Is.EqualTo(before));
    }

    [Test]
    public async Task AnUnreadableSavesFolder_IsSavesUnreadable_ForEveryOperation()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        AddSaves(game.Zip, null, (LibraryZip.SavesIndexEntry, "{ no"u8.ToArray()));
        var anyId = GameSaves.NewId();

        Assert.That(new[]
        {
            (await Editor.AddSaveAsync(game.Id, game.RomIds[0], "state", "S", null, Upload([1]), Alice, default)).Code,
            (await Editor.UpdateSaveAsync(game.Id, anyId, "T", null, Alice, AliceViewer, default)).Code,
            (await Editor.ReplaceSaveFileAsync(game.Id, anyId, Upload([2]), Alice, AliceViewer, default)).Code,
            (await Editor.DeleteSaveAsync(game.Id, anyId, Alice, AliceViewer, default)).Code,
        }, Is.All.EqualTo(SavesUnreadable));
    }

    [Test]
    public async Task ALegacyGame_IsStorageMigrationPending_EvenWhenItsSavesAreUnreadable()
    {
        var bytes = TestLibrary.RomBytes(++_seed);
        var game = TestLibrary.Game("Old", TestLibrary.Rom("Old (USA).nds", bytes)) with { System = LegacyExternalTestHandler.SystemId };
        var zip = Path.Combine(Roms, "legacy", "O", "Old.zip");
        LegacyZipWriter.Write(zip, game, new Dictionary<string, byte[]> { [LibraryZip.SavesIndexEntry] = "{ no"u8.ToArray() }, bytes);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, "legacy/O/Old.zip", new FileInfo(zip).Length, DateTime.UtcNow), default);
        var romId = (await Service<IRomRepository>().ListByGameAsync(game.GameId, default)).Single().Id;

        var result = await Editor.AddSaveAsync(game.GameId, romId, "state", "S", null, Upload([1]), Alice, default);

        Assert.That(result.Code, Is.EqualTo(StorageMigrationPending));
    }

    [Test]
    public async Task DeletingASavesRelease_LeavesAnOrphan_DeletingTheLastReleaseRemovesTheSavesWithTheGame()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb", "Tetris (Japan).gb");
        var save = (await Editor.AddSaveAsync(game.Id, game.RomIds[1], "state", "S", null, Upload([1]), Alice, default)).Save!;

        // Rom ids change on every mirror rewrite, so each delete looks its release up again.
        Assert.That((await Editor.DeleteReleaseAsync(game.Id, await RomId(game.Id, "Tetris (Japan).gb"), Alice, default)).Code, Is.Null);
        var saves = ReadSaves(game.Zip).Index!.Saves;
        Assert.That(saves.Select(s => (s.Id, s.Release)), Is.EqualTo(new[] { (save.Id, "Tetris (Japan).gb") }), "kept as an orphan");

        Assert.That((await Editor.DeleteReleaseAsync(game.Id, await RomId(game.Id, "Tetris (World).gb"), Alice, default)).Code, Is.Null);
        Assert.That(File.Exists(game.Zip), Is.False, "the game's ZIP, saves and all, is gone");
    }

    [Test]
    public async Task AnOrphan_ReLinks_WhenAReleaseOfThatNameJoins_InAnyCase()
    {
        var target = await WriteGame("Tetris", "Tetris (World).gb");
        var source = await WriteGame("Other", "Other (World).gb", "Tetris (Japan).gb");
        AddSaves(target.Zip, [.. Enumerable.Range(0, 25).Select(_ => Record("TETRIS (JAPAN).GB") with { UploadedBy = "alice" })]);

        Assert.That((await Editor.MoveReleaseAsync(source.Id, source.RomIds[1], target.Id, null, Alice, default)).Code, Is.Null);

        var full = await Editor.AddSaveAsync(target.Id, await RomId(target.Id, "Tetris (Japan).gb"), "state", "S", null, Upload([1]), Alice, default);
        Assert.That(full.Code, Is.EqualTo(SaveLimitReached), "the 25 orphans now count toward the release that joined");
    }

    // --- User Management S1–S4: private saves ---

    private static readonly AuditActor Bob = new(null, "bob", null);
    private static readonly SaveViewer BobViewer = new("bob", false);
    private static readonly SaveViewer AdminViewer = new("boss", true);

    [Test]
    public async Task ASiteUser_ActsOnTheirOwnSave_AnotherUsersOrAnUnownedSaveIsNotFound()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var bobs = (await Editor.AddSaveAsync(game.Id, game.RomIds[0], "state", "Bob's", null, Upload([1]), Bob, default)).Save!;
        var unowned = Record("Tetris (World).gb");
        var ownedByAlice = (await Editor.AddSaveAsync(game.Id, game.RomIds[0], "state", "Mine", null, Upload([1]), Alice, default)).Save!;
        AddSavesToIndex(game, unowned);

        foreach (var id in new[] { bobs.Id, unowned.Id })
        {
            Assert.That(await Editor.PrecheckSaveAsync(game.Id, null, id, AliceViewer, default), Is.EqualTo(new LibraryEditResult(NotFound)));
            Assert.That(new[]
            {
                (await Editor.UpdateSaveAsync(game.Id, id, "T", null, Alice, AliceViewer, default)).Code,
                (await Editor.ReplaceSaveFileAsync(game.Id, id, Upload([2]), Alice, AliceViewer, default)).Code,
                (await Editor.DeleteSaveAsync(game.Id, id, Alice, AliceViewer, default)).Code,
            }, Is.All.EqualTo(NotFound));
        }
        Assert.That(await Editor.PrecheckSaveAsync(game.Id, null, ownedByAlice.Id, new SaveViewer("ALICE", false), default), Is.Null);
        Assert.That((await Editor.UpdateSaveAsync(game.Id, ownedByAlice.Id, "Renamed", null, Alice, AliceViewer, default)).Code, Is.Null);
        Assert.That(ReadSaves(game.Zip).Index!.Saves.Select(s => s.Name), Is.EquivalentTo(new[] { "Bob's", "Renamed", "Old" }));
    }

    [Test]
    public async Task ANonOwnedId_AnswersExactlyLikeAnUnknownId_WhileAJobRuns_AndWithUnreadableSaves()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var bobs = (await Editor.AddSaveAsync(game.Id, game.RomIds[0], "state", "Bob's", null, Upload([1]), Bob, default)).Save!;
        var unknown = GameSaves.NewId();

        async Task<(LibraryEditResult?, string?, string?)> Outcome(string id) => (
            await Editor.PrecheckSaveAsync(game.Id, null, id, AliceViewer, default),
            (await Editor.UpdateSaveAsync(game.Id, id, "T", null, Alice, AliceViewer, default)).Code,
            (await Editor.DeleteSaveAsync(game.Id, id, Alice, AliceViewer, default)).Code);

        Assert.That(await Outcome(bobs.Id), Is.EqualTo(await Outcome(unknown)));
        using (Service<JobLock>().TryAcquire())
            Assert.That(await Outcome(bobs.Id), Is.EqualTo(await Outcome(unknown)));
        using (var archive = ZipFile.Open(game.Zip, ZipArchiveMode.Update))
            archive.GetEntry(LibraryZip.SavesIndexEntry)!.Delete();
        AddSaves(game.Zip, null, (LibraryZip.SavesIndexEntry, "{ no"u8.ToArray()));
        Assert.That(await Outcome(bobs.Id), Is.EqualTo(await Outcome(unknown)));
        Assert.That((await Outcome(bobs.Id)).Item2, Is.EqualTo(SavesUnreadable));
    }

    [Test]
    public async Task AnAdmin_EditsAnotherUsersSave_KeepingItsOwner()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        var bobs = (await Editor.AddSaveAsync(game.Id, game.RomIds[0], "state", "Bob's", null, Upload([1]), Bob, default)).Save!;
        var boss = new AuditActor(null, "boss", null);

        var updated = await Editor.UpdateSaveAsync(game.Id, bobs.Id, "Checked", null, boss, AdminViewer, default);

        Assert.That((updated.Code, updated.Save!.UploadedBy, updated.Save.UpdatedBy), Is.EqualTo(((string?)null, (string?)"bob", (string?)"boss")));
        Assert.That((await Editor.DeleteSaveAsync(game.Id, bobs.Id, boss, AdminViewer, default)).Code, Is.Null);
    }

    [Test]
    public async Task TheLimit_CountsOnlyTheUploadersOwnSaves_ForAdminsToo()
    {
        var game = await WriteGame("Tetris", "Tetris (World).gb");
        AddSaves(game.Zip, [.. Enumerable.Range(0, 25).Select(_ => Record("Tetris (World).gb") with { UploadedBy = "bob" })]);

        Assert.That(await Editor.PrecheckSaveAsync(game.Id, game.RomIds[0], null, AliceViewer, default), Is.Null);
        Assert.That((await Editor.AddSaveAsync(game.Id, game.RomIds[0], "state", "S", null, Upload([1]), Alice, default)).Code, Is.Null);
        var romId = await RomId(game.Id, "Tetris (World).gb");
        Assert.That((await Editor.PrecheckSaveAsync(game.Id, romId, null, new SaveViewer("bob", true), default))!.Code, Is.EqualTo(SaveLimitReached));
        var bobFull = await Editor.AddSaveAsync(game.Id, romId, "state", "S", null, Upload([1]), Bob, default);
        Assert.That((bobFull.Code, bobFull.Details), Is.EqualTo((SaveLimitReached, "You already have 25 saves for this release. Delete one first.")));
    }

    /// <summary>Adds records (one-byte entries) to a ZIP that the editor already gave a <c>saves.json</c>.</summary>
    private void AddSavesToIndex(TestGame game, params GameSave[] records)
    {
        var current = ReadSaves(game.Zip).Index!.Saves;
        using (var archive = ZipFile.Open(game.Zip, ZipArchiveMode.Update))
            archive.GetEntry(LibraryZip.SavesIndexEntry)!.Delete();
        AddSaves(game.Zip, null, [(LibraryZip.SavesIndexEntry, GameSaves.Serialize([.. current, .. records])!), .. records.Select(r => (r.Entry, new byte[] { 7 }))]);
    }
}
