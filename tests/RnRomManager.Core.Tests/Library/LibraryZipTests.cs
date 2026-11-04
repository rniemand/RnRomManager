using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NSubstitute;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Spec §6.1 atomic writes, §3.1 FileShare + move retry, §9.3 read limits; §15.1 "ZIP writer" rows without the DB.</summary>
public class LibraryZipTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid GameId = Guid.Parse("6f1f6c3e-8a54-4f39-9d2b-0d2a7c1e9b11");

    private TempDir _dir = null!;
    private InfoJsonReader _reader = null!;
    private LibraryZipWriter _writer = null!;
    private string _target = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _reader = Reader();
        _writer = new LibraryZipWriter(_reader);
        _target = _dir.Combine("Tetris.zip");
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static InfoJsonReader Reader(params IInfoJsonUpgrader[] upgraders)
    {
        var handler = Substitute.For<ISystemHandler>();
        handler.Id.Returns("gb");
        var registry = Substitute.For<ISystemRegistry>();
        registry.All.Returns([handler]);
        return new InfoJsonReader(upgraders, registry);
    }

    private static byte[] RomBytes(int seed) => new SyntheticRomBuilder { Seed = seed }.Build();

    private static InfoJsonRom Rom(string fileName, byte[] bytes, bool isPrimary = true) => new()
    {
        FileName = fileName,
        OriginalFileName = fileName,
        BaseTitle = "Tetris",
        Regions = ["World"],
        Languages = [],
        Revision = null,
        Flags = [],
        IsPrimary = isPrimary,
        Identification = new InfoJsonIdentification(IdentificationMethod.Manual),
        Header = null,
        Metadata = new InfoJsonRomMetadata(
            bytes.Length,
            Crc32.ToHex(bytes),
            Convert.ToHexStringLower(MD5.HashData(bytes)),
            Convert.ToHexStringLower(SHA1.HashData(bytes)),
            When, When, "/app/ingest/gb/" + fileName),
    };

    private static InfoJsonImage BoxArt(byte[] bytes) => new()
    {
        FileName = "boxart-01.png",
        Type = ImageType.BoxArt,
        Source = InfoJsonImage.SourceDefault,
        SourceUrl = null,
        IsDefault = true,
        SizeBytes = bytes.Length,
        Sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)),
        Width = 1,
        Height = 1,
        DateAdded = When,
    };

    private static InfoJsonGame Game(IReadOnlyList<InfoJsonRom> roms, IReadOnlyList<InfoJsonImage>? images = null) => new()
    {
        SchemaVersion = InfoJsonReader.CurrentVersion,
        GameId = GameId,
        System = "gb",
        Title = "Tetris",
        DisplayTitle = "Tetris",
        ReleaseYear = null,
        Developers = [],
        Publishers = [],
        Genres = [],
        Description = null,
        Roms = roms,
        Images = images ?? [],
        Metadata = new InfoJsonGameMetadata(When, When, "0.1.0"),
    };

    private static Func<Stream> Content(byte[] bytes) => () => new MemoryStream(bytes);

    private static readonly byte[] Png = PngTests.Png(1, 1);

    /// <summary>A new game with one ROM + box art at <see cref="_target"/>.</summary>
    private async Task<(InfoJsonGame Game, byte[] Rom)> WriteNewGameAsync()
    {
        var rom = RomBytes(1);
        var game = Game([Rom("Tetris (World).gb", rom)], [BoxArt(Png)]);
        await _writer.WriteAsync(_target, null, new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>
        {
            ["roms/Tetris (World).gb"] = Content(rom),
            ["images/boxart-01.png"] = Content(Png),
        }));
        return (game, rom);
    }

    private static byte[] ReadEntry(LibraryZip zip, string name)
    {
        using var stream = zip.OpenEntry(name, long.MaxValue);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Writes a ZIP by hand (bypassing the writer) so tests can plant bad content.</summary>
    private static void WriteRawZip(string path, params (string Name, byte[] Bytes)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    }

    [Test]
    public async Task NewGame_RoundTripsInfoJsonAndEntries()
    {
        var (game, rom) = await WriteNewGameAsync();

        using var zip = LibraryZip.Open(_target, _reader);
        Assert.That(zip.InfoJson!.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok));
        Assert.That(InfoJsonSerializer.Serialize(zip.InfoJson.Game!), Is.EqualTo(InfoJsonSerializer.Serialize(game)));
        Assert.That(zip.EntryNames, Is.EquivalentTo(new[] { "info.json", "roms/Tetris (World).gb", "images/boxart-01.png" }));
        Assert.That(ReadEntry(zip, "roms/Tetris (World).gb"), Is.EqualTo(rom));
        Assert.That(ReadEntry(zip, "images/boxart-01.png"), Is.EqualTo(Png));
        Assert.That(File.Exists(_target + ".tmp"), Is.False);
    }

    [Test]
    public async Task Write_UsesDeflateAndUtf8WithoutBom()
    {
        await WriteNewGameAsync();

        using var archive = ZipFile.OpenRead(_target);
        var info = archive.GetEntry("info.json")!;
        Assert.That(info.CompressedLength, Is.LessThan(info.Length), "Deflate");
        using var stream = info.Open();
        var first = new byte[3];
        stream.ReadExactly(first);
        Assert.That(first, Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Test]
    public async Task NewRelease_CopiesExistingEntriesFromTheZip()
    {
        var (game, oldRom) = await WriteNewGameAsync();
        var newRom = RomBytes(2);
        var updated = game with { Roms = [.. game.Roms, Rom("Tetris (Japan).gb", newRom, isPrimary: false)] };

        using (var existing = LibraryZip.Open(_target, _reader))
        {
            await _writer.WriteAsync(_target, existing, new LibraryZipChanges(updated, new Dictionary<string, Func<Stream>>
            {
                ["roms/Tetris (Japan).gb"] = Content(newRom),
            }));
        }

        using var zip = LibraryZip.Open(_target, _reader);
        Assert.That(zip.InfoJson!.Game!.Roms, Has.Count.EqualTo(2));
        Assert.That(ReadEntry(zip, "roms/Tetris (World).gb"), Is.EqualTo(oldRom));
        Assert.That(ReadEntry(zip, "roms/Tetris (Japan).gb"), Is.EqualTo(newRom));
        Assert.That(ReadEntry(zip, "images/boxart-01.png"), Is.EqualTo(Png));
    }

    [Test]
    public async Task EntriesNoLongerListed_AreDropped()
    {
        var (game, _) = await WriteNewGameAsync();
        var withoutArt = game with { Images = [] };

        using (var existing = LibraryZip.Open(_target, _reader))
            await _writer.WriteAsync(_target, existing, new LibraryZipChanges(withoutArt, new Dictionary<string, Func<Stream>>()));

        using var zip = LibraryZip.Open(_target, _reader);
        Assert.That(zip.EntryNames, Is.EquivalentTo(new[] { "info.json", "roms/Tetris (World).gb" }));
    }

    [Test]
    public async Task PathChange_WritesAtTheNewPathAndKeepsTheOld()
    {
        var (game, rom) = await WriteNewGameAsync();
        var newTarget = _dir.Combine("Tetris DX.zip");

        using (var existing = LibraryZip.Open(_target, _reader))
            await _writer.WriteAsync(newTarget, existing, new LibraryZipChanges(game with { Title = "Tetris DX" }, new Dictionary<string, Func<Stream>>()));

        // The caller commits the DB, then deletes the old ZIP (§6.1).
        Assert.That(File.Exists(_target), Is.True);
        using var zip = LibraryZip.Open(newTarget, _reader);
        Assert.That(zip.InfoJson!.Game!.Title, Is.EqualTo("Tetris DX"));
        Assert.That(ReadEntry(zip, "roms/Tetris (World).gb"), Is.EqualTo(rom));
    }

    [Test]
    public async Task NewEntryWithWrongHash_VerifyFailed_OriginalIntact()
    {
        var (game, _) = await WriteNewGameAsync();
        var before = await File.ReadAllBytesAsync(_target);
        var claimed = RomBytes(2);
        var actual = RomBytes(3);
        var updated = game with { Roms = [.. game.Roms, Rom("Tetris (Japan).gb", claimed, isPrimary: false)] };

        using (var existing = LibraryZip.Open(_target, _reader))
        {
            var ex = await Assert.ThrowsAsync<LibraryZipVerifyException>(() => _writer.WriteAsync(_target, existing,
                new LibraryZipChanges(updated, new Dictionary<string, Func<Stream>> { ["roms/Tetris (Japan).gb"] = Content(actual) })));
            Assert.That(ex!.EntryName, Is.EqualTo("roms/Tetris (Japan).gb"));
        }

        Assert.That(await File.ReadAllBytesAsync(_target), Is.EqualTo(before));
        Assert.That(File.Exists(_target + ".tmp"), Is.False);
    }

    [Test]
    public async Task CorruptExistingEntry_VerifyFailedNamingThatEntry()
    {
        // The ZIP's info.json says one thing; the stored ROM bytes (same size) are another.
        var listed = RomBytes(1);
        var stored = RomBytes(9);
        var game = Game([Rom("Tetris (World).gb", listed)]);
        WriteRawZip(_target, ("info.json", InfoJsonSerializer.Serialize(game)), ("roms/Tetris (World).gb", stored));
        var before = await File.ReadAllBytesAsync(_target);
        var newRom = RomBytes(2);
        var updated = game with { Roms = [.. game.Roms, Rom("Tetris (Japan).gb", newRom, isPrimary: false)] };

        using (var existing = LibraryZip.Open(_target, _reader))
        {
            var ex = await Assert.ThrowsAsync<LibraryZipVerifyException>(() => _writer.WriteAsync(_target, existing,
                new LibraryZipChanges(updated, new Dictionary<string, Func<Stream>> { ["roms/Tetris (Japan).gb"] = Content(newRom) })));
            Assert.That(ex!.EntryName, Is.EqualTo("roms/Tetris (World).gb"));
            Assert.That(ex.Message, Does.Contain("SHA1"));
        }

        Assert.That(await File.ReadAllBytesAsync(_target), Is.EqualTo(before));
        Assert.That(File.Exists(_target + ".tmp"), Is.False);
    }

    [Test]
    public async Task ExistingEntryLargerThanListed_VerifyFailedNamingThatEntry()
    {
        var listed = RomBytes(1);
        var game = Game([Rom("Tetris (World).gb", listed)]);
        WriteRawZip(_target, ("info.json", InfoJsonSerializer.Serialize(game)), ("roms/Tetris (World).gb", [.. listed, 0]));

        using var existing = LibraryZip.Open(_target, _reader);
        var ex = await Assert.ThrowsAsync<LibraryZipVerifyException>(() =>
            _writer.WriteAsync(_target, existing, new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>())));
        Assert.That(ex!.EntryName, Is.EqualTo("roms/Tetris (World).gb"));
    }

    [Test]
    public async Task InvalidNewInfoJson_VerifyFailed_NothingWritten()
    {
        var rom = RomBytes(1);
        var game = Game([Rom("Tetris (World).gb", rom, isPrimary: false)]); // no primary: fails §6.2

        var ex = await Assert.ThrowsAsync<LibraryZipVerifyException>(() => _writer.WriteAsync(_target, null,
            new LibraryZipChanges(game, new Dictionary<string, Func<Stream>> { ["roms/Tetris (World).gb"] = Content(rom) })));

        Assert.That(ex!.EntryName, Is.EqualTo("info.json"));
        Assert.That(ex.Message, Does.Contain("isPrimary"));
        Assert.That(Directory.GetFiles(_dir.Path), Is.Empty);
    }

    [Test]
    public async Task ListedEntryWithNoContent_Throws()
    {
        var game = Game([Rom("Tetris (World).gb", RomBytes(1))]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _writer.WriteAsync(_target, null, new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>())));
        Assert.That(Directory.GetFiles(_dir.Path), Is.Empty);
    }

    [Test]
    public async Task UnlistedNewEntry_Throws()
    {
        var rom = RomBytes(1);
        var game = Game([Rom("Tetris (World).gb", rom)]);

        await Assert.ThrowsAsync<ArgumentException>(() => _writer.WriteAsync(_target, null, new LibraryZipChanges(game,
            new Dictionary<string, Func<Stream>> { ["roms/Tetris (World).gb"] = Content(rom), ["roms/extra.gb"] = Content(rom) })));
    }

    [Test]
    public async Task NewerSchemaExisting_IsRefused()
    {
        var (game, _) = await WriteNewGameAsync();
        var json = JsonNode.Parse(InfoJsonSerializer.Serialize(game))!;
        json["schemaVersion"] = InfoJsonReader.CurrentVersion + 1;
        var newer = _dir.Combine("Newer.zip");
        WriteRawZip(newer, ("info.json", Encoding.UTF8.GetBytes(json.ToJsonString())));

        using var existing = LibraryZip.Open(newer, _reader);
        Assert.That(existing.InfoJson!.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.NewerSchema));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _writer.WriteAsync(newer, existing, new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>())));
    }

    [Test]
    public async Task Open_RunsTheUpgradeChain()
    {
        var (game, _) = await WriteNewGameAsync();
        var json = JsonNode.Parse(InfoJsonSerializer.Serialize(game))!;
        json["schemaVersion"] = 0;
        var old = _dir.Combine("Old.zip");
        WriteRawZip(old, ("info.json", Encoding.UTF8.GetBytes(json.ToJsonString())));
        var upgrader = Substitute.For<IInfoJsonUpgrader>();
        upgrader.FromVersion.Returns(0);

        using var zip = LibraryZip.Open(old, Reader(upgrader));

        upgrader.Received(1).Upgrade(Arg.Any<JsonObject>());
        Assert.That(zip.InfoJson!.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Ok));
        Assert.That(zip.InfoJson.FileVersion, Is.EqualTo(0));
    }

    [Test]
    public void Open_NoInfoJson_IsNull()
    {
        WriteRawZip(_target, ("roms/x.gb", RomBytes(1)));

        using var zip = LibraryZip.Open(_target, _reader);
        Assert.That(zip.InfoJson, Is.Null);
    }

    [Test]
    public void Open_OversizeInfoJson_IsInvalidWithoutReadingItAll()
    {
        WriteRawZip(_target, ("info.json", new byte[InfoJsonReader.MaxBytes + 10]));

        using var zip = LibraryZip.Open(_target, _reader);
        Assert.That(zip.InfoJson!.Kind, Is.EqualTo(InfoJsonReadResult.Outcome.Invalid));
        Assert.That(zip.InfoJson.Rule, Does.Contain("larger than"));
    }

    [Test]
    public void Open_NotAZip_ThrowsInvalidData()
    {
        File.WriteAllBytes(_target, RomBytes(1));

        Assert.Throws<InvalidDataException>(() => LibraryZip.Open(_target, _reader).Dispose());
    }

    [Test]
    public void OpenEntry_EnforcesActualBytesAndMissingEntries()
    {
        WriteRawZip(_target, ("roms/x.gb", new byte[100]));
        using var zip = LibraryZip.Open(_target, _reader);

        using (var stream = zip.OpenEntry("roms/x.gb", 99))
            Assert.Throws<LimitExceededException>(() => stream.CopyTo(Stream.Null));
        Assert.Throws<FileNotFoundException>(() => zip.OpenEntry("roms/y.gb", 100));
    }

    [Test]
    [Platform(Exclude = "Win")] // D44: Windows can't replace an open file; there it's retried, then IoError.
    public async Task Replace_WorksWhileAReaderHasTheZipOpen()
    {
        var (game, _) = await WriteNewGameAsync();
        using var reader = LibraryZip.Open(_target, _reader); // e.g. a download streaming it (§3.1)
        using var existing = LibraryZip.Open(_target, _reader);

        await _writer.WriteAsync(_target, existing, new LibraryZipChanges(game with { Title = "Tetris 2" }, new Dictionary<string, Func<Stream>>()));

        using var zip = LibraryZip.Open(_target, _reader);
        Assert.That(zip.InfoJson!.Game!.Title, Is.EqualTo("Tetris 2"));
    }

    [Test]
    [Platform("Win")] // Linux lets the rename replace an open file; only Windows can block it.
    public async Task Move_RetriesWhileTheTargetIsLocked()
    {
        var (game, _) = await WriteNewGameAsync();
        var locked = new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = Task.Delay(700).ContinueWith(_ => locked.Dispose(), TaskScheduler.Default);

        await _writer.WriteAsync(_target, null, new LibraryZipChanges(game with { Title = "Tetris 2" }, new Dictionary<string, Func<Stream>>
        {
            ["roms/Tetris (World).gb"] = Content(RomBytes(1)),
            ["images/boxart-01.png"] = Content(Png),
        }));

        using var zip = LibraryZip.Open(_target, _reader);
        Assert.That(zip.InfoJson!.Game!.Title, Is.EqualTo("Tetris 2"));
    }

    [Test]
    [Platform("Win")]
    public async Task Move_GivesUpAfterThreeRetries_TmpDeleted()
    {
        var (game, _) = await WriteNewGameAsync();
        var before = await File.ReadAllBytesAsync(_target);
        using var locked = new FileStream(_target, FileMode.Open, FileAccess.Read, FileShare.Read);

        await Assert.ThrowsAsync(Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>(), () =>
            _writer.WriteAsync(_target, null, new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>
            {
                ["roms/Tetris (World).gb"] = Content(RomBytes(1)),
                ["images/boxart-01.png"] = Content(Png),
            })));

        locked.Dispose();
        Assert.That(await File.ReadAllBytesAsync(_target), Is.EqualTo(before));
        Assert.That(File.Exists(_target + ".tmp"), Is.False);
    }
}
