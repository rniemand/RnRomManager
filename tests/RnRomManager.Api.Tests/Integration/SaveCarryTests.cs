using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
using static RnRomManager.Core.Library.LibraryEditResult;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>B5: merge and a last-release move carry the source's saves into the target, renamed with their releases.</summary>
[Category("Integration")]
[TestFixture(false)]
[TestFixture(true)]
public class SaveCarryTests(bool external)
{
    private static readonly AuditActor Alice = new(null, "alice", null);
    private static readonly SaveViewer AliceViewer = new("alice", false);

    private ApiFactory _factory = null!;
    private int _seed;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();
    private LibraryEditor Editor => Service<LibraryEditor>();

    private string System => external ? ExternalTestHandler.SystemId : "gb";
    private string Ext => external ? ".nsp" : ".gb";

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => SystemHandlerRegistration.AddSystemHandler<ExternalTestHandler>(services),
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public Task SetUp() => _factory.ResetAsync();

    /// <summary>A mirrored game of this fixture's system; external releases are files next to the ZIP.</summary>
    private async Task<SaveCarryTestGame> WriteGame(string title, params string[] stems)
    {
        var bytes = stems.Select(_ => TestLibrary.RomBytes(++_seed)).ToArray();
        var roms = stems.Select((s, i) =>
        {
            var rom = TestLibrary.Rom(s + Ext, bytes[i], isPrimary: i == 0);
            return external ? rom with { ExternalFileName = ExternalRomName.Build(title, rom.FileName) } : rom;
        }).ToArray();
        var game = TestLibrary.Game(title, roms) with { System = System };
        var zipPath = $"{System}/{title[..1]}/{title}.zip";
        var zip = Path.GetFullPath(Path.Combine(_factory.Root, "roms", zipPath));
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        var entries = new Dictionary<string, Func<Stream>>();
        for (var i = 0; i < roms.Length; i++)
        {
            var content = bytes[i];
            if (external)
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(zip)!, roms[i].ExternalFileName!), content);
            else
                entries[LibraryZip.RomEntry(roms[i].FileName)] = () => new MemoryStream(content);
        }
        await new LibraryZipWriter(Service<InfoJsonReader>()).WriteAsync(zip, null, new LibraryZipChanges(game, entries));
        var file = new FileInfo(zip);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, zipPath, file.Length, file.LastWriteTimeUtc), default);
        return new(game.GameId, zip);
    }

    private async Task<long> RomId(Guid gameId, string stem) =>
        (await Service<IRomRepository>().ListByGameAsync(gameId, default)).Single(r => r.FileName == stem + Ext).Id;

    private SaveUpload Upload(byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, $"upload-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(path, bytes);
        return new(path, ".state", bytes.Length, Convert.ToHexStringLower(SHA1.HashData(bytes)));
    }

    private async Task<GameSave> AddSave(
        SaveCarryTestGame game,
        string stem,
        string name,
        byte[] bytes)
    {
        var result = await Editor.AddSaveAsync(game.Id, await RomId(game.Id, stem), "state", name, null, Upload(bytes), Alice, default);
        Assert.That(result.Code, Is.Null, result.Details);
        return result.Save!;
    }

    private (InfoJsonGame Game, IReadOnlyList<GameSave> Saves) Read(string zip)
    {
        using var library = LibraryZip.Open(zip, Service<InfoJsonReader>());
        var saves = GameSaves.Read(library);
        Assert.That(saves.State, Is.Not.EqualTo(SavesState.Unreadable), saves.Problem);
        return (library.InfoJson!.Game!, saves.Index?.Saves ?? []);
    }

    private static byte[] Entry(string zip, string name)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var stream = archive.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The release of <paramref name="game"/> whose SHA-1 is the one the source had under <paramref name="sourceName"/>.</summary>
    private static string NameOf(InfoJsonGame game, InfoJsonGame source, string sourceName) =>
        game.Roms.Single(r => r.Metadata.Sha1 == source.Roms.Single(s => s.FileName == sourceName).Metadata.Sha1).FileName;

    [Test]
    public async Task MovingARelease_LeavesItsSavesInTheSource()
    {
        var source = await WriteGame("Alpha", "Shared (World)", "Alpha (World)");
        var target = await WriteGame("Beta", "Beta (World)");
        var save = await AddSave(source, "Shared (World)", "S", [1, 2]);

        Assert.That((await Editor.MoveReleaseAsync(source.Id, await RomId(source.Id, "Shared (World)"), target.Id, null, Alice, default)).Code, Is.Null);

        Assert.That(Read(source.Zip).Saves.Select(s => s.Id), Is.EqualTo(new[] { save.Id }));
        Assert.That(Read(target.Zip).Saves, Is.Empty);
    }

    [Test]
    public async Task MovingTheLastRelease_CarriesItsSaves_WithTheReleaseRenamed()
    {
        var source = await WriteGame("Alpha", "Shared (World)");
        var target = await WriteGame("Beta", "Shared (World)");
        var before = Read(source.Zip).Game;
        var save = await AddSave(source, "Shared (World)", "S", [1, 2]);
        var own = await AddSave(target, "Shared (World)", "Own", [3]);

        Assert.That((await Editor.MoveReleaseAsync(source.Id, await RomId(source.Id, "Shared (World)"), target.Id, null, Alice, default)).Code, Is.Null);

        Assert.That(File.Exists(source.Zip), Is.False);
        var (game, saves) = Read(target.Zip);
        var renamed = NameOf(game, before, "Shared (World)" + Ext);
        Assert.That(renamed, Is.Not.EqualTo("Shared (World)" + Ext), "the clash renamed it");
        Assert.That(saves.Select(s => (s.Id, s.Release)), Is.EqualTo(new[] { (own.Id, "Shared (World)" + Ext), (save.Id, renamed) }));
        Assert.That(Entry(target.Zip, save.Entry), Is.EqualTo(new byte[] { 1, 2 }));
    }

    [Test]
    public async Task Merge_CarriesSaves_RenamesTheirReleases_KeepsOrphans_AndReIdsAClash()
    {
        var source = await WriteGame("Alpha", "Shared (World)", "Gone (World)");
        var target = await WriteGame("Beta", "Shared (World)");
        var before = Read(source.Zip).Game;
        var linked = await AddSave(source, "Shared (World)", "Linked", [1]);
        var orphan = await AddSave(source, "Gone (World)", "Orphan", [2]);
        Assert.That((await Editor.DeleteReleaseAsync(source.Id, await RomId(source.Id, "Gone (World)"), Alice, default)).Code, Is.Null);
        var own = await AddSave(target, "Shared (World)", "Own", [3]);
        // The target holds a save with the source's id: the carried one gets a new id.
        SetSaveId(target.Zip, own.Id, linked.Id);

        Assert.That((await Editor.MergeAsync(source.Id, target.Id, Alice, default)).Code, Is.Null);

        var (game, saves) = Read(target.Zip);
        var renamed = NameOf(game, before, "Shared (World)" + Ext);
        Assert.That(saves.Select(s => s.Name), Is.EqualTo(new[] { "Own", "Linked", "Orphan" }));
        Assert.That(saves[0].Id, Is.EqualTo(linked.Id));
        Assert.That(saves[1].Id, Is.Not.EqualTo(linked.Id), "re-ided on the clash");
        Assert.That((saves[1].Release, saves[2].Release), Is.EqualTo((renamed, orphan.Release)), "the orphan stays as it was");
        Assert.That((Entry(target.Zip, saves[1].Entry), Entry(target.Zip, saves[2].Entry)), Is.EqualTo((new byte[] { 1 }, new byte[] { 2 })));
    }

    /// <summary>Release editing B16: turns the ZIP's saves into a version 1 folder by hand (flat entries, the same records).</summary>
    private void ToVersion1(string zip)
    {
        var (_, saves) = Read(zip);
        var legacy = saves.Select(s => s with { Legacy = true }).ToArray();
        var bytes = saves.Select(s => Entry(zip, s.Entry)).ToArray();
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith(LibraryZip.SavesFolder, StringComparison.Ordinal)).ToList())
            entry.Delete();
        using (var index = archive.CreateEntry(LibraryZip.SavesIndexEntry).Open())
            index.Write(TestSaves.V1Index(legacy));
        for (var i = 0; i < legacy.Length; i++)
        {
            using var entry = archive.CreateEntry(legacy[i].Entry).Open();
            entry.Write(bytes[i]);
        }
    }

    /// <summary>The ZIP's saves.json is version 2 and every save's bytes are in its owner's folder.</summary>
    private void AssertVersion2(string zip, params (string Id, byte[] Bytes)[] expected)
    {
        using (var archive = ZipFile.OpenRead(zip))
        using (var index = archive.GetEntry(LibraryZip.SavesIndexEntry)!.Open())
            Assert.That(global::System.Text.Json.JsonDocument.Parse(index).RootElement.GetProperty("version").GetInt32(), Is.EqualTo(2));
        var (_, saves) = Read(zip);
        foreach (var (id, bytes) in expected)
        {
            var save = saves.Single(s => s.Id == id);
            Assert.That(save.Entry, Does.StartWith("saves/alice/"));
            Assert.That(Entry(zip, save.Entry), Is.EqualTo(bytes));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Merge_OfAVersion1Source_IntoVersion1OrVersion2_WritesVersion2(bool targetV1)
    {
        var source = await WriteGame("Alpha", "Shared (World)");
        var target = await WriteGame("Beta", "Other (World)");
        var carried = await AddSave(source, "Shared (World)", "Carried", [1, 2]);
        var own = await AddSave(target, "Other (World)", "Own", [3]);
        ToVersion1(source.Zip);
        if (targetV1)
            ToVersion1(target.Zip);

        Assert.That((await Editor.MergeAsync(source.Id, target.Id, Alice, default)).Code, Is.Null);

        AssertVersion2(target.Zip, (own.Id, [3]), (carried.Id, [1, 2]));
    }

    [Test]
    public async Task LastReleaseMove_OfAVersion1Source_WritesVersion2()
    {
        var source = await WriteGame("Alpha", "Shared (World)");
        var target = await WriteGame("Beta", "Other (World)");
        var carried = await AddSave(source, "Shared (World)", "Carried", [4]);
        ToVersion1(source.Zip);

        Assert.That((await Editor.MoveReleaseAsync(source.Id, await RomId(source.Id, "Shared (World)"), target.Id, null, Alice, default)).Code, Is.Null);

        AssertVersion2(target.Zip, (carried.Id, [4]));
    }

    /// <summary>Rewrites a save's id (and its entry name) in the ZIP by hand.</summary>
    private void SetSaveId(string zip, string from, string to)
    {
        var (_, saves) = Read(zip);
        var save = saves.Single(s => s.Id == from);
        var bytes = Entry(zip, save.Entry);
        var changed = save with { Id = to };
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
        archive.GetEntry(save.Entry)!.Delete();
        archive.GetEntry(LibraryZip.SavesIndexEntry)!.Delete();
        using (var entry = archive.CreateEntry(changed.Entry).Open())
            entry.Write(bytes);
        using (var index = archive.CreateEntry(LibraryZip.SavesIndexEntry).Open())
            index.Write(GameSaves.Serialize([.. saves.Select(s => s.Id == from ? changed : s)])!);
    }

    private static void BreakSaves(string zip)
    {
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
        archive.GetEntry(LibraryZip.SavesIndexEntry)?.Delete();
        using var entry = archive.CreateEntry(LibraryZip.SavesIndexEntry).Open();
        entry.Write("{ no"u8);
    }

    [TestCase(true, TestName = "{m} source side")]
    [TestCase(false, TestName = "{m} target side")]
    public async Task MergeAndLastReleaseMove_WithUnreadableSaves_AreRefusedBeforeAnyWrite(bool sourceSide)
    {
        var source = await WriteGame("Alpha", "Alpha (World)");
        var target = await WriteGame("Beta", "Beta (World)");
        await AddSave(source, "Alpha (World)", "S", [1]);
        BreakSaves(sourceSide ? source.Zip : target.Zip);
        var (sourceBytes, targetBytes) = (File.ReadAllBytes(source.Zip), File.ReadAllBytes(target.Zip));
        var files = Directory.GetFiles(Path.GetDirectoryName(source.Zip)!).Concat(Directory.GetFiles(Path.GetDirectoryName(target.Zip)!)).Order().ToList();

        var merge = await Editor.MergeAsync(source.Id, target.Id, Alice, default);
        var move = await Editor.MoveReleaseAsync(source.Id, await RomId(source.Id, "Alpha (World)"), target.Id, null, Alice, default);

        Assert.That((merge.Code, move.Code), Is.EqualTo((SavesUnreadable, SavesUnreadable)));
        Assert.That((File.ReadAllBytes(source.Zip), File.ReadAllBytes(target.Zip)), Is.EqualTo((sourceBytes, targetBytes)));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(source.Zip)!).Concat(Directory.GetFiles(Path.GetDirectoryName(target.Zip)!)).Order(),
            Is.EqualTo(files), "no file moved");
    }

    [Test]
    public async Task Merge_ThatWouldGrowTheIndexOverOneMiB_IsSavesIndexFull()
    {
        var source = await WriteGame("Alpha", "Alpha (World)");
        var target = await WriteGame("Beta", "Beta (World)");
        var added = await AddSave(source, "Alpha (World)", "S", [1]);
        var carried = (await Editor.UpdateSaveAsync(source.Id, added.Id, null, new string('d', GameSaves.MaxDescriptionLength), Alice, AliceViewer, default)).Save!;
        // Fill the target's index with records no bigger than the carried one, until one more would pass 1 MiB.
        var record = carried with { Release = "Gone" };
        var filler = new List<GameSave>();
        while (GameSaves.Serialize(filler) is not null)
            filler.Add(record with { Id = GameSaves.NewId() });
        filler.RemoveAt(filler.Count - 1);
        using (var archive = ZipFile.Open(target.Zip, ZipArchiveMode.Update))
        {
            using (var index = archive.CreateEntry(LibraryZip.SavesIndexEntry).Open())
                index.Write(GameSaves.Serialize(filler)!);
            foreach (var save in filler)
            {
                using var entry = archive.CreateEntry(save.Entry).Open();
                entry.Write([1]);
            }
        }
        var targetBytes = File.ReadAllBytes(target.Zip);
        Assert.That(GameSaves.Serialize([.. filler, carried]), Is.Null, "test setup: the carried record overflows");

        var merge = await Editor.MergeAsync(source.Id, target.Id, Alice, default);

        Assert.That(merge.Code, Is.EqualTo(SavesIndexFull));
        Assert.That(File.ReadAllBytes(target.Zip), Is.EqualTo(targetBytes));
    }
}
