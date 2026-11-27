using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.Systems.Sega;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Library;

/// <summary>SG-1000 S11: <c>MoveToSg1000Async</c> moves one Master System game of <c>.sg</c> dumps to SG-1000.</summary>
public class Sg1000MoverTests
{
    private const string OldPath = "sms/X/X.zip";
    private const string NewPath = "sg1000/X/X.zip";
    private static readonly AuditActor Actor = new(null, "system", null);
    private static readonly byte[] SaveBytes = [1, 2, 3];
    private static readonly byte[] Uploaded = PngTests.Png(64, 64);

    private TempDir _dir = null!;
    private InfoJsonReader _reader = null!;
    private ILibraryMirror _mirror = null!;
    private IAuditLog _audit = null!;
    private LibraryEditor _editor = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        var systems = new SystemRegistry([new MasterSystemHandler(), new Sg1000Handler()]);
        _reader = new InfoJsonReader([], systems);
        _mirror = Substitute.For<ILibraryMirror>();
        _audit = Substitute.For<IAuditLog>();
        var paths = Options.Create(new PathsOptions { Roms = _dir.Combine("roms"), Ingest = _dir.Combine("ingest"), Cache = _dir.Combine("cache") });
        var media = TestMedia.Offline();
        var retry = new GameMediaRetry(_mirror, _reader, systems, media, paths, _audit, TimeProvider.System, NullLogger<GameMediaRetry>.Instance);
        _editor = new LibraryEditor(new JobLock(), Substitute.For<IGameRepository>(), Substitute.For<IRomRepository>(), Substitute.For<IImageRepository>(),
            _mirror, _reader, new DatCache(paths, systems, NullLogger<DatCache>.Instance), systems, media, retry, paths, _audit, new AuditPaths(paths),
            Substitute.For<ICollectionRepository>(), TimeProvider.System, NullLogger<LibraryEditor>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private string Full(string zipPath) => _dir.Combine("roms", zipPath);

    private static GameSave Save(string release) => new()
    {
        Id = GameSaves.NewId(),
        Name = "Before boss",
        Description = "",
        Kind = GameSaves.KindState,
        Release = release,
        Extension = ".state",
        SizeBytes = SaveBytes.Length,
        Sha1 = Convert.ToHexStringLower(SHA1.HashData(SaveBytes)),
        UploadedBy = "alice",
        UploadedAt = When,
        UpdatedBy = null,
        UpdatedAt = When,
    };

    /// <summary>A Master System game with the placeholder box art (cover), an uploaded box art and a note on its first release.</summary>
    private static InfoJsonGame Game(params (string Name, string Original)[] releases)
    {
        var placeholder = MediaService.Default(new MasterSystemHandler()).ToInfoJson(When, isCover: true);
        var upload = new MediaImage(ImageType.BoxArt, Uploaded, 64, 64, null, false, InfoJsonImage.SourceUpload).ToInfoJson(When, isCover: false)
            with { FileName = "boxart-02.png" };
        return TestLibrary.Game("X", [.. releases.Select((r, i) => Rom(r.Name, RomBytes(100 + i), isPrimary: i == 0) with { OriginalFileName = r.Original })])
            with { System = "sms", Images = [placeholder, upload] };
    }

    private static InfoJsonGame Default() => Game(("X (Japan).sms", "x.sg"), ("X (Japan) (Rev 1).sms", "x1.SG"));

    private async Task<GameRow> SeedAsync(InfoJsonGame game, string zipPath = OldPath, bool save = false)
    {
        var path = Full(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = game.Roms.Select((r, i) => (LibraryZip.RomEntry(r.FileName), RomBytes(100 + i)))
            .Append((LibraryZip.NoteEntry(game.Roms[0].FileName), "hi"u8.ToArray()))
            .Append((LibraryZip.ImageEntry("boxart-02.png"), Uploaded))
            .ToDictionary(e => e.Item1, e => (Func<Stream>)(() => new MemoryStream(e.Item2)));
        if (game.Images.FirstOrDefault(i => i.IsDefault) is { } placeholder)
            content[LibraryZip.ImageEntry(placeholder.FileName)] = () => new MemoryStream(new MasterSystemHandler().DefaultBoxArtPng.ToArray());
        SavesChange? saves = null;
        if (save)
        {
            var record = Save(game.Roms[0].FileName);
            saves = new SavesChange(GameSaves.Serialize([record])!, new Dictionary<string, Func<Stream>> { [record.Entry] = () => new MemoryStream(SaveBytes) },
                new HashSet<string>());
        }
        await new LibraryZipWriter(_reader).WriteAsync(path, null, new LibraryZipChanges(game, content, Saves: saves));
        return Row(game.GameId, zipPath);
    }

    private static GameRow Row(Guid id, string zipPath) => new()
    {
        Id = id, SystemId = "sms", Title = "X", DisplayTitle = "X", ZipPath = zipPath, Bucket = "X", Developers = [], Publishers = [], Genres = [],
        ZipSizeBytes = 1, ZipMtimeUtc = DateTime.UtcNow, InfoSchemaVersion = InfoJsonReader.CurrentVersion, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private (InfoJsonGame Game, IReadOnlyList<string> Entries, LibraryZip Zip) Open(string zipPath)
    {
        var zip = LibraryZip.Open(Full(zipPath), _reader);
        return (zip.InfoJson!.Game!, zip.EntryNames, zip);
    }

    private Task<LibraryEditResult> MoveAsync(GameRow row) => _editor.MoveToSg1000Async(row, Actor, default);

    private void AssertNothingWritten(string before, byte[] bytes)
    {
        Assert.That(File.ReadAllBytes(Full(before)), Is.EqualTo(bytes));
        Assert.That(Directory.Exists(_dir.Combine("roms", "sg1000", "X")) && Directory.GetFiles(_dir.Combine("roms", "sg1000", "X")).Length > 0, Is.False);
        _ = _mirror.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
        _audit.DidNotReceiveWithAnyArgs().Record(default!);
    }

    [Test]
    public async Task Move_RenamesReleases_CarriesNoteAndSave_SwapsThePlaceholder_AndDeletesTheOldZip()
    {
        var row = await SeedAsync(Default(), save: true);

        var result = await MoveAsync(row);

        Assert.That(result.Code, Is.Null, result.Details);
        Assert.That(File.Exists(Full(OldPath)), Is.False);
        var (game, entries, zip) = Open(NewPath);
        using (zip)
        {
            Assert.That((game.System, game.GameId), Is.EqualTo(("sg1000", row.Id)));
            Assert.That(game.Roms.Select(r => r.FileName), Is.EqualTo(new[] { "X (Japan).sg", "X (Japan) (Rev 1).sg" }));
            Assert.That(game.Roms.Select(r => r.OriginalFileName), Is.EqualTo(new[] { "x.sg", "x1.SG" }));
            Assert.That(entries, Does.Contain(LibraryZip.RomEntry("X (Japan).sg")).And.Contain(LibraryZip.NoteEntry("X (Japan).sg"))
                .And.Not.Contain(LibraryZip.RomEntry("X (Japan).sms")).And.Not.Contain(LibraryZip.NoteEntry("X (Japan).sms")));
            Assert.That(GameSaves.Read(zip).Index!.Saves.Single().Release, Is.EqualTo("X (Japan).sg"));

            var art = new Sg1000Handler().DefaultBoxArtPng.ToArray();
            var placeholder = game.Images.Single(i => i.IsDefault);
            Assert.That((placeholder.FileName, placeholder.IsCover, placeholder.SizeBytes, placeholder.Sha1),
                Is.EqualTo(("boxart-01.png", true, (long)art.Length, Convert.ToHexStringLower(SHA1.HashData(art)))));
            using var stream = zip.OpenEntry(LibraryZip.ImageEntry("boxart-01.png"), art.Length);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Assert.That(bytes.ToArray(), Is.EqualTo(art));
            Assert.That(game.Images.Single(i => !i.IsDefault).FileName, Is.EqualTo("boxart-02.png"));
        }
        await _mirror.Received(1).ReplaceAsync(Arg.Is<MirrorEntry>(m => m.ZipPath == NewPath), Arg.Any<CancellationToken>());
        _audit.Received(1).Record(Arg.Is<AuditEvent>(e => e.Action == AuditActions.GameUpdated && e.SystemId == "sg1000" && e.GameId == row.Id
            && e.Path == "roms/" + OldPath && e.TargetPath == "roms/" + NewPath && e.Details == "Moved from Master System to SG-1000"));
    }

    [Test]
    public async Task AReleaseAlreadyNamedSg_KeepsItsName()
    {
        var row = await SeedAsync(Game(("X (Japan).sg", "x.sg"), ("X (Japan) (Rev 1).sms", "x1.sg")));

        Assert.That((await MoveAsync(row)).Code, Is.Null);

        using var zip = Open(NewPath).Zip;
        Assert.That(zip.InfoJson!.Game!.Roms.Select(r => r.FileName), Is.EqualTo(new[] { "X (Japan).sg", "X (Japan) (Rev 1).sg" }));
    }

    [Test]
    public async Task AnSmsOriginal_IsNotSg1000_AndWritesNothing()
    {
        var row = await SeedAsync(Game(("X (Japan).sms", "x.sg"), ("X (Japan) (Rev 1).sms", "x1.sms")));
        var before = File.ReadAllBytes(Full(OldPath));

        Assert.That((await MoveAsync(row)).Code, Is.EqualTo(LibraryEditResult.NotSg1000));
        AssertNothingWritten(OldPath, before);
    }

    [Test]
    public async Task AReleaseIdentifiedByTheDat_IsNotSg1000()
    {
        var game = Default();
        game = game with { Roms = [game.Roms[0] with { Identification = new InfoJsonIdentification(IdentificationMethod.Dat, "Sega - Master System - Mark III", "1", "X") }, game.Roms[1]] };
        var row = await SeedAsync(game);
        var before = File.ReadAllBytes(Full(OldPath));

        Assert.That((await MoveAsync(row)).Code, Is.EqualTo(LibraryEditResult.NotSg1000));
        AssertNothingWritten(OldPath, before);
    }

    [Test]
    public async Task AnotherGameIdInTheZip_IsNotSg1000_AndWritesNothing()
    {
        var row = await SeedAsync(Default());
        var before = File.ReadAllBytes(Full(OldPath));

        Assert.That((await MoveAsync(row with { Id = Guid.NewGuid() })).Code, Is.EqualTo(LibraryEditResult.NotSg1000));
        AssertNothingWritten(OldPath, before);
    }

    [Test]
    public async Task AMissingZip_IsNotSg1000() =>
        Assert.That((await MoveAsync(Row(Guid.NewGuid(), OldPath))).Code, Is.EqualTo(LibraryEditResult.NotSg1000));

    [Test]
    public async Task AnUnreadableSavesIndex_IsSavesUnreadable_AndWritesNothing()
    {
        var row = await SeedAsync(Default());
        using (var archive = ZipFile.Open(Full(OldPath), ZipArchiveMode.Update))
        {
            using var stream = archive.CreateEntry(LibraryZip.SavesIndexEntry).Open();
            stream.Write("{ not json"u8);
        }
        var before = File.ReadAllBytes(Full(OldPath));

        Assert.That((await MoveAsync(row)).Code, Is.EqualTo(LibraryEditResult.SavesUnreadable));
        AssertNothingWritten(OldPath, before);
    }

    [Test]
    public async Task AClashWithAnotherGame_GetsASuffix()
    {
        await SeedAsync(Default() with { System = "sg1000", Roms = [Rom("Other.sg", RomBytes(100))] }, NewPath);
        var row = await SeedAsync(Default());

        Assert.That((await MoveAsync(row)).Code, Is.Null);

        Assert.That(File.Exists(Full("sg1000/X/X (2).zip")), Is.True);
    }

    [Test]
    public async Task AFailedMirror_IsDbWriteFailed_DeletesTheNewZip_AndKeepsTheOldOne()
    {
        var row = await SeedAsync(Default());
        var before = File.ReadAllBytes(Full(OldPath));
        _mirror.ReplaceAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));

        Assert.That((await MoveAsync(row)).Code, Is.EqualTo(LibraryEditResult.DbWriteFailed));

        Assert.That(File.Exists(Full(NewPath)), Is.False);
        Assert.That(File.ReadAllBytes(Full(OldPath)), Is.EqualTo(before));
        _audit.DidNotReceiveWithAnyArgs().Record(default!);
    }

    [Test]
    public async Task ARerunAfterACrash_ReusesTheLeftover()
    {
        var game = Default();
        await SeedAsync(game with { System = "sg1000", Roms = [.. game.Roms.Select(r => r with { FileName = Path.ChangeExtension(r.FileName, ".sg") })] }, NewPath);
        var row = await SeedAsync(game);

        Assert.That((await MoveAsync(row)).Code, Is.Null);

        Assert.That(Directory.GetFiles(_dir.Combine("roms", "sg1000", "X")).Select(Path.GetFileName), Is.EqualTo(new[] { "X.zip" }));
        using (var zip = Open(NewPath).Zip)
            Assert.That(zip.InfoJson!.Game!.Roms.Select(r => r.OriginalFileName), Is.EqualTo(new[] { "x.sg", "x1.SG" }));
        _audit.ReceivedWithAnyArgs(1).Record(default!);
    }

    [Test]
    public async Task WithNoSg1000Folder_TheMoveCreatesIt()
    {
        var row = await SeedAsync(Default());
        Assert.That(Directory.Exists(_dir.Combine("roms", "sg1000")), Is.False);

        Assert.That((await MoveAsync(row)).Code, Is.Null);

        Assert.That(File.Exists(Full(NewPath)), Is.True);
    }

    [Test]
    public async Task AFileWhereTheBucketFolderGoes_IsIoError_AndWritesNothing()
    {
        var row = await SeedAsync(Default());
        Directory.CreateDirectory(_dir.Combine("roms", "sg1000"));
        await File.WriteAllTextAsync(_dir.Combine("roms", "sg1000", "X"), "not a folder");
        var before = File.ReadAllBytes(Full(OldPath));

        Assert.That((await MoveAsync(row)).Code, Is.EqualTo(LibraryEditResult.IoError));

        Assert.That(File.ReadAllBytes(Full(OldPath)), Is.EqualTo(before));
        _ = _mirror.DidNotReceiveWithAnyArgs().ReplaceAsync(default!, default);
        _audit.DidNotReceiveWithAnyArgs().Record(default!);
    }
}
