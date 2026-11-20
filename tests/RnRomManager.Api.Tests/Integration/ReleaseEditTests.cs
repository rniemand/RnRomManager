using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;
using static RnRomManager.Core.Library.LibraryEditResult;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Release editing B5, B6, B8–B10: <see cref="LibraryEditor.EditReleaseAsync"/> on embedded, external and disc (FileSets) games.</summary>
[Category("Integration")]
[TestFixture("gb")]
[TestFixture(ExternalTestHandler.SystemId)]
[TestFixture(ExternalSetTestHandler.SystemId)]
public class ReleaseEditTests(string system)
{
    private static readonly AuditActor Boss = new(null, "boss", null);

    private ApiFactory _factory = null!;
    private int _seed;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();
    private LibraryEditor Editor => Service<LibraryEditor>();

    private bool External => system != "gb";
    private bool FileSets => system == ExternalSetTestHandler.SystemId;
    private string Ext => system switch { "gb" => ".gb", ExternalTestHandler.SystemId => ".nsp", _ => ".iso" };

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services =>
            {
                SystemHandlerRegistration.AddSystemHandler<ExternalTestHandler>(services);
                SystemHandlerRegistration.AddSystemHandler<ExternalSetTestHandler>(services);
            },
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public Task SetUp() => _factory.ResetAsync();

    private sealed record TestGame(Guid Id, string Zip, string ZipPath);

    /// <summary>A mirrored game "Tetris" whose releases are <paramref name="roms"/> (file names without the extension).</summary>
    private async Task<TestGame> WriteGame(string? chosenStem, params Func<InfoJsonRom, InfoJsonRom>[] roms)
    {
        const string title = "Tetris";
        var list = new List<(InfoJsonRom Rom, byte[] Bytes)>();
        foreach (var shape in roms)
        {
            var bytes = TestLibrary.RomBytes(++_seed);
            var rom = shape(TestLibrary.Rom("x" + Ext, bytes, isPrimary: list.Count == 0) with { BaseTitle = title });
            rom = rom with { FileName = rom.FileName.EndsWith(Ext, StringComparison.Ordinal) ? rom.FileName : rom.FileName + Ext };
            if (External)
                rom = rom with { ExternalFileName = FileSets ? rom.FileName : ExternalRomName.Build(title, rom.FileName) };
            list.Add((rom, bytes));
        }
        var game = TestLibrary.Game(title, [.. list.Select(r => r.Rom)]) with
        {
            System = system,
            ChosenPrimarySha1 = chosenStem is null ? null : list.Single(r => r.Rom.FileName == chosenStem + Ext).Rom.Metadata.Sha1,
        };
        var zipPath = $"{system}/T/{title}.zip";
        var zip = Path.GetFullPath(Path.Combine(_factory.Root, "roms", zipPath));
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        var entries = new Dictionary<string, Func<Stream>>();
        foreach (var (rom, bytes) in list)
        {
            if (External)
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(zip)!, rom.ExternalFileName!), bytes);
            else
                entries[LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(bytes);
        }
        await new LibraryZipWriter(Service<InfoJsonReader>()).WriteAsync(zip, null, new LibraryZipChanges(game, entries));
        var file = new FileInfo(zip);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, zipPath, file.Length, file.LastWriteTimeUtc), default);
        return new(game.GameId, zip, zipPath);
    }

    private static Func<InfoJsonRom, InfoJsonRom> Rom(string stem, Func<InfoJsonRom, InfoJsonRom>? more = null) =>
        r => (more ?? (x => x))(r with { FileName = stem });

    private async Task<long> RomId(Guid gameId, string fileName) =>
        (await Service<IRomRepository>().ListByGameAsync(gameId, default)).Single(r => r.FileName == fileName).Id;

    private async Task<LibraryEditResult> Edit(TestGame game, string stem, ReleaseEdit patch) =>
        await Editor.EditReleaseAsync(game.Id, await RomId(game.Id, stem + Ext), patch, Boss, default);

    private InfoJsonGame Read(TestGame game)
    {
        using var zip = LibraryZip.Open(game.Zip, Service<InfoJsonReader>());
        return zip.InfoJson!.Game!;
    }

    private IReadOnlyList<GameSave> Saves(TestGame game)
    {
        using var zip = LibraryZip.Open(game.Zip, Service<InfoJsonReader>());
        return GameSaves.Read(zip).Index?.Saves ?? [];
    }

    private static List<string> Entries(TestGame game)
    {
        using var archive = ZipFile.OpenRead(game.Zip);
        return [.. archive.Entries.Select(e => e.FullName)];
    }

    private async Task<GameSave> AddSave(TestGame game, string stem)
    {
        var bytes = new byte[] { 7, 7 };
        var path = Path.Combine(_factory.Root, $"upload-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(path, bytes);
        var result = await Editor.AddSaveAsync(game.Id, await RomId(game.Id, stem + Ext), "state", "S", null,
            new SaveUpload(path, ".state", bytes.Length, Convert.ToHexStringLower(SHA1.HashData(bytes))), Boss, default);
        Assert.That(result.Code, Is.Null, result.Details);
        return result.Save!;
    }

    private async Task<List<(string Action, string? Path, string? Details)>> AuditRows()
    {
        await Service<AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        return [.. await db.QueryAsync<(string, string?, string?)>(
            "SELECT action, path, details FROM audit_events WHERE action = 'ReleaseEdited' ORDER BY id")];
    }

    private void NotOnDiscSystems()
    {
        if (FileSets)
            Assert.Ignore("Disc releases keep their file name (B8).");
    }

    [Test]
    public async Task Rename_FollowsTheTags_AndCarriesNoteAndSaves()
    {
        NotOnDiscSystems();
        var game = await WriteGame(null, Rom("Tetris (World)"), Rom("Tetris (Japan)", r => r with { Regions = ["Japan"] }));
        Assert.That((await Editor.SaveNoteAsync(game.Id, await RomId(game.Id, "Tetris (World)" + Ext), "my note", null, default)).Code, Is.Null);
        var save = await AddSave(game, "Tetris (World)");
        var externalBefore = Read(game).Roms[0].ExternalFileName;

        var result = await Edit(game, "Tetris (World)", new ReleaseEdit { Hack = true });

        var renamed = "Tetris (World) (Hack)" + Ext;
        Assert.That((result.Code, result.FileName), Is.EqualTo(((string?)null, renamed)), result.Details);
        var rom = Read(game).Roms[0];
        Assert.That((rom.FileName, rom.Flags), Is.EqualTo((renamed, new[] { "Hack" })));
        Assert.That(rom.ExternalFileName, Is.EqualTo(externalBefore), "an external file keeps its name on disk");
        var entries = Entries(game);
        Assert.That(entries, Does.Contain(LibraryZip.NoteEntry(renamed)).And.Not.Contain(LibraryZip.NoteEntry("Tetris (World)" + Ext)));
        if (!External)
            Assert.That(entries, Does.Contain(LibraryZip.RomEntry(renamed)).And.Not.Contain(LibraryZip.RomEntry("Tetris (World)" + Ext)));
        Assert.That(Saves(game).Single(s => s.Id == save.Id).Release, Is.EqualTo(renamed));
        Assert.That((await Service<IRomRepository>().ListByGameAsync(game.Id, default)).Select(r => r.FileName), Does.Contain(renamed));
        Assert.That(await AuditRows(), Is.EqualTo(new[]
            { (AuditActions.ReleaseEdited, (string?)$"roms/{game.ZipPath}", (string?)$"Tetris (World){Ext} → {renamed}") }));
    }

    [Test]
    public async Task Rename_ThatNoSaveNames_LeavesSavesJsonAsItIs()
    {
        NotOnDiscSystems();
        var game = await WriteGame(null, Rom("Tetris (World)"), Rom("Tetris (Japan)", r => r with { Regions = ["Japan"] }));
        await AddSave(game, "Tetris (Japan)");
        byte[] Index()
        {
            using var archive = ZipFile.OpenRead(game.Zip);
            using var stream = archive.GetEntry(LibraryZip.SavesIndexEntry)!.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        var before = Index();

        Assert.That((await Edit(game, "Tetris (World)", new ReleaseEdit { Translated = true })).Code, Is.Null);

        Assert.That(Index(), Is.EqualTo(before));
    }

    [Test]
    public async Task Rename_OntoAnotherReleasesName_GetsTheSuffix()
    {
        NotOnDiscSystems();
        var game = await WriteGame(null, Rom("Tetris (World)"), Rom("Tetris (World) (Hack)", r => r with { Flags = ["Hack"] }));

        var result = await Edit(game, "Tetris (World)", new ReleaseEdit { Hack = true });

        Assert.That(result.FileName, Is.EqualTo("Tetris (World) (Hack) (2)" + Ext));
        Assert.That(Read(game).Roms.Select(r => r.FileName), Is.EqualTo(new[] { "Tetris (World) (Hack) (2)" + Ext, "Tetris (World) (Hack)" + Ext }));
    }

    [Test]
    public async Task DiscRelease_KeepsItsName_TagsChange()
    {
        if (!FileSets)
            Assert.Ignore("disc systems only");
        var game = await WriteGame(null, Rom("Tetris (World) (Disc 1)", r => r with { Flags = ["Disc 1", "Beta"] }));

        var result = await Edit(game, "Tetris (World) (Disc 1)", new ReleaseEdit { SetDisc = true, Disc = 2, Hack = true, Regions = ["World", "USA"] });

        Assert.That(result.FileName, Is.EqualTo("Tetris (World) (Disc 1)" + Ext));
        var rom = Read(game).Roms.Single();
        Assert.That((rom.FileName, rom.ExternalFileName, rom.Flags, rom.Regions),
            Is.EqualTo(("Tetris (World) (Disc 1)" + Ext, "Tetris (World) (Disc 1)" + Ext, new[] { "Disc 2", "Beta", "Hack" }, new[] { "World", "USA" })));
    }

    [Test]
    public async Task Flags_KeepTheirOrder_B5()
    {
        var game = await WriteGame(null, Rom("Tetris (World) (Beta) (Hack)", r => r with { Flags = ["Beta", "Hack"] }));

        Assert.That((await Edit(game, "Tetris (World) (Beta) (Hack)", new ReleaseEdit { Hack = false, Translated = true, Kind = "dlc" })).Code, Is.Null);

        Assert.That(Read(game).Roms.Single().Flags, Is.EqualTo(new[] { "Beta", "Translated", "DLC" }));
    }

    [Test]
    public async Task NoChange_WritesNothing_AndRecordsNoAudit_B6()
    {
        var game = await WriteGame(null, Rom("Tetris (World)"));
        var written = File.GetLastWriteTimeUtc(game.Zip);
        var bytes = File.ReadAllBytes(game.Zip);

        var result = await Edit(game, "Tetris (World)", new ReleaseEdit { Hack = false, Regions = ["World"], Kind = "release" });

        Assert.That((result.Code, result.FileName), Is.EqualTo(((string?)null, "Tetris (World)" + Ext)));
        Assert.That((File.GetLastWriteTimeUtc(game.Zip), File.ReadAllBytes(game.Zip)), Is.EqualTo((written, bytes)));
        Assert.That(await AuditRows(), Is.Empty);
    }

    [Test]
    public async Task UpdateToRelease_ClearsTheVersion()
    {
        NotOnDiscSystems();
        var game = await WriteGame(null, Rom("Orbit [0100000000003800][v65536][1.0.1]", r => r with { Flags = ["Update"], Version = "1.0.1", Regions = [] }));

        var result = await Edit(game, "Orbit [0100000000003800][v65536][1.0.1]", new ReleaseEdit { Kind = "release" });

        Assert.That(result.FileName, Is.EqualTo("Orbit [0100000000003800][v65536]" + Ext));
        var rom = Read(game).Roms.Single();
        Assert.That((rom.Flags, rom.Version), Is.EqualTo((Array.Empty<string>(), (string?)null)));
    }

    [Test]
    public async Task ChosenPrimary_TurnedIntoAnAddOn_IsNoLongerChosen()
    {
        var game = await WriteGame("Tetris (Japan)", Rom("Tetris (World)"), Rom("Tetris (Japan)", r => r with { Regions = ["Japan"] }));
        Assert.That(Read(game).Roms.Single(r => r.IsPrimary).FileName, Is.EqualTo("Tetris (World)" + Ext), "written as the fixture wrote it");

        Assert.That((await Edit(game, "Tetris (Japan)", new ReleaseEdit { Kind = "dlc" })).Code, Is.Null);

        var after = Read(game);
        Assert.That((after.ChosenPrimarySha1, after.Roms.Single(r => r.IsPrimary).FileName), Is.EqualTo(((string?)null, "Tetris (World)" + Ext)));
    }

    [Test]
    public async Task UnreadableSaves_RefuseARename_ButNotATagsOnlyEdit()
    {
        NotOnDiscSystems();
        var game = await WriteGame(null, Rom("Tetris (World)"));
        using (var archive = ZipFile.Open(game.Zip, ZipArchiveMode.Update))
        using (var entry = archive.CreateEntry(LibraryZip.SavesIndexEntry).Open())
            entry.Write("not json"u8);
        var file = new FileInfo(game.Zip);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(Read(game), game.ZipPath, file.Length, file.LastWriteTimeUtc), default);

        Assert.That((await Edit(game, "Tetris (World)", new ReleaseEdit { Hack = true })).Code, Is.EqualTo(SavesUnreadable));
        var tagsOnly = await Edit(game, "Tetris (World)", new ReleaseEdit { Kind = "dlc" });
        Assert.That((tagsOnly.Code, tagsOnly.FileName), Is.EqualTo(((string?)null, "Tetris (World)" + Ext)), tagsOnly.Details);
        Assert.That(await AuditRows(), Is.EqualTo(new[]
            { (AuditActions.ReleaseEdited, (string?)$"roms/{game.ZipPath}", (string?)("Tetris (World)" + Ext)) }));
    }

    [Test]
    public async Task GameChecks_AreValidationErrors()
    {
        var game = await WriteGame(null, Rom("Tetris (Mars)", r => r with { Regions = ["Mars"] }));

        var disc = await Edit(game, "Tetris (Mars)", new ReleaseEdit { SetDisc = true, Disc = 2 });
        var region = await Edit(game, "Tetris (Mars)", new ReleaseEdit { Regions = ["Venus"] });
        var version = await Edit(game, "Tetris (Mars)", new ReleaseEdit { SetVersion = true, Version = "1.0.1" });
        var kept = await Edit(game, "Tetris (Mars)", new ReleaseEdit { Regions = ["Mars", "USA"] });

        if (FileSets)
            Assert.That(disc.Code, Is.Null);
        else
            Assert.That(disc.Errors!["disc"], Is.EqualTo(new[] { "Disc numbers are for disc systems." }));
        Assert.That(region.Errors!["regions"], Is.EqualTo(new[] { "Must be known No-Intro regions." }));
        Assert.That(version.Errors!["version"], Is.EqualTo(new[] { "Only an update has a version." }));
        Assert.That(kept.Code, Is.Null, "a region the release already has is accepted");
        Assert.That(Read(game).Roms.Single().Regions, Is.EqualTo(new[] { "Mars", "USA" }));
    }
}
