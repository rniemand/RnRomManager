using System.IO.Compression;
using System.Security.Cryptography;
using NSubstitute;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Library;

/// <summary>B3: how every ZIP write carries, replaces or drops the <c>saves/</c> folder, through both writers.</summary>
[TestFixture(false)]
[TestFixture(true)]
public class LibraryZipSavesTests(bool useCheckedWrite)
{
    private static readonly DateTimeOffset When = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RomBytes = TestLibrary.RomBytes(1);
    private static readonly IReadOnlyDictionary<string, Func<Stream>> NoEntries = new Dictionary<string, Func<Stream>>();

    private TempDir _dir = null!;
    private string _root = null!;
    private string _zip = null!;
    private InfoJsonReader _reader = null!;
    private InfoJsonGame _game = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dir = new TempDir();
        _root = _dir.Combine("roms");
        _zip = Path.Combine(_root, "gb", "T", "Tetris.zip");
        var gb = TestHandlers.Fake("gb", embedRomsInZip: true);
        var registry = Substitute.For<ISystemRegistry>();
        registry.All.Returns([gb]);
        _reader = new InfoJsonReader([], registry);
        _game = TestLibrary.Game("Tetris", TestLibrary.Rom("Tetris (World).gb", RomBytes));
        Directory.CreateDirectory(Path.GetDirectoryName(_zip)!);
        await new LibraryZipWriter(_reader).WriteAsync(_zip, null, new LibraryZipChanges(_game,
            new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry("Tetris (World).gb")] = () => new MemoryStream(RomBytes) }));
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static GameSave Save(byte[] bytes, string extension = ".state") => new()
    {
        Id = GameSaves.NewId(), Name = "Before boss", Description = "", Kind = GameSaves.KindState, Release = "Tetris (World).gb",
        Extension = extension, SizeBytes = bytes.Length, Sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)), UploadedBy = "alice",
        UploadedAt = When, UpdatedBy = "alice", UpdatedAt = When,
    };

    /// <summary>Adds entries to the ZIP by hand (the test plays a ZIP an earlier write or a person left).</summary>
    private void AddEntries(params (string Name, byte[] Bytes)[] entries)
    {
        using var archive = ZipFile.Open(_zip, ZipArchiveMode.Update);
        foreach (var (name, bytes) in entries)
        {
            using var entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
            entry.Write(bytes);
        }
    }

    private Dictionary<string, byte[]> Entries()
    {
        using var archive = ZipFile.OpenRead(_zip);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });
    }

    private Dictionary<string, byte[]> SavesEntries() =>
        Entries().Where(e => e.Key.StartsWith(LibraryZip.SavesFolder, StringComparison.Ordinal)).ToDictionary();

    /// <summary>A write of this fixture's kind from the current ZIP.</summary>
    private async Task Write(Func<InfoJsonGame, InfoJsonGame>? change = null, SavesChange? saves = null)
    {
        var existing = LibraryZip.Open(_zip, _reader);
        var changes = new LibraryZipChanges(change?.Invoke(existing.InfoJson!.Game!) ?? existing.InfoJson!.Game!, NoEntries, Saves: saves);
        var writer = new LibraryZipWriter(_reader);
        try
        {
            if (useCheckedWrite)
                await writer.WriteCheckedAsync(_root, _zip, existing, changes);
            else
                await writer.WriteAsync(_zip, existing, changes);
        }
        finally
        {
            existing.Dispose();
        }
    }

    private static Task Unrelated(LibraryZipSavesTests t) => t.Write(g => g with { Rating = 5 });

    [Test]
    public async Task UnrelatedWrite_CopiesAValidIndexAndItsEntries_AndDropsUnlistedOnes()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var save = Save(bytes);
        var index = GameSaves.Serialize([save])!;
        AddEntries((LibraryZip.SavesIndexEntry, index), (save.Entry, bytes), ("saves/stray.bin", [9]));

        await Unrelated(this);

        var saves = SavesEntries();
        Assert.That(saves.Keys.Order(), Is.EqualTo(new[] { save.Entry, LibraryZip.SavesIndexEntry }.Order()));
        Assert.That(saves[LibraryZip.SavesIndexEntry], Is.EqualTo(index));
        Assert.That(saves[save.Entry], Is.EqualTo(bytes));
    }

    /// <summary>Release editing B16: a write that isn't about saves leaves a version 1 index and its flat entries as they are.</summary>
    [Test]
    public async Task UnrelatedWrite_CopiesAVersion1IndexAndItsEntriesUnchanged()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var save = Save(bytes) with { Legacy = true, UploadedBy = null };
        var index = TestSaves.V1Index(save);
        AddEntries((LibraryZip.SavesIndexEntry, index), (save.Entry, bytes));

        await Unrelated(this);

        var saves = SavesEntries();
        Assert.That(saves.Keys.Order(), Is.EqualTo(new[] { $"saves/{save.Id}.state", LibraryZip.SavesIndexEntry }.Order()));
        Assert.That(saves[LibraryZip.SavesIndexEntry], Is.EqualTo(index));
        Assert.That(saves[save.Entry], Is.EqualTo(bytes));
    }

    [Test]
    public async Task UnrelatedWrite_InvalidIndex_CopiesEverySavesEntryByteForByte()
    {
        var garbage = "{ not json"u8.ToArray();
        AddEntries((LibraryZip.SavesIndexEntry, garbage), ("saves/a.state", [1, 2]), ("saves/b", [3]));

        await Unrelated(this);

        var saves = SavesEntries();
        Assert.That(saves.Keys.Order(), Is.EqualTo(new[] { "saves/a.state", "saves/b", LibraryZip.SavesIndexEntry }.Order()));
        Assert.That(saves[LibraryZip.SavesIndexEntry], Is.EqualTo(garbage));
        Assert.That(saves["saves/a.state"], Is.EqualTo(new byte[] { 1, 2 }));
    }

    [Test]
    public async Task UnrelatedWrite_MissingIndexWithEntries_CopiesThemByteForByte()
    {
        AddEntries(("saves/a.state", [1, 2]));

        await Unrelated(this);

        Assert.That(SavesEntries().Keys, Is.EqualTo(new[] { "saves/a.state" }));
    }

    [Test]
    public async Task UnrelatedWrite_IndexListingAMissingOrWrongLengthEntry_IsUnreadable_AndCopiedByteForByte()
    {
        var listed = Save([1, 2, 3]);
        var missing = Save([4, 5]);
        var index = GameSaves.Serialize([listed, missing])!;
        AddEntries((LibraryZip.SavesIndexEntry, index), (listed.Entry, [1, 2, 3, 4]));

        await Unrelated(this);

        var saves = SavesEntries();
        Assert.That(saves.Keys.Order(), Is.EqualTo(new[] { listed.Entry, LibraryZip.SavesIndexEntry }.Order()));
        Assert.That(saves[listed.Entry], Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    }

    [Test]
    public async Task UnrelatedWrite_ACorruptEntryInTheByteForByteCase_FailsNamingIt_AndLeavesTheZip()
    {
        // A deflated entry whose first compressed byte names a reserved block type: reading it is InvalidDataException.
        using (var archive = ZipFile.Open(_zip, ZipArchiveMode.Update))
        using (var entry = archive.CreateEntry("saves/a.state", CompressionLevel.Optimal).Open())
            entry.Write(new byte[1000]);
        var bytes = File.ReadAllBytes(_zip);
        var name = bytes.AsSpan().IndexOf("saves/a.state"u8); // the local header's copy comes first
        var header = name - 30;
        var data = name + BitConverter.ToUInt16(bytes, header + 26) + BitConverter.ToUInt16(bytes, header + 28);
        bytes[data] = 0xFF;
        File.WriteAllBytes(_zip, bytes);

        var ex = await Assert.ThrowsAsync<LibraryZipVerifyException>(() => Unrelated(this));

        Assert.That(ex!.EntryName, Is.EqualTo("saves/a.state"));
        Assert.That(File.ReadAllBytes(_zip), Is.EqualTo(bytes));
    }

    [Test]
    public async Task UnrelatedWrite_WithoutSaves_WritesNoSavesEntry_AndKeepsTheOthers()
    {
        var before = Entries();

        await Write();

        var after = Entries();
        Assert.That(after.Keys.Order(), Is.EqualTo(before.Keys.Order()));
        Assert.That(after.Keys.Where(k => k.StartsWith(LibraryZip.SavesFolder, StringComparison.Ordinal)), Is.Empty);
        Assert.That(after.Where(e => e.Key != LibraryZip.InfoJsonEntry).ToDictionary(), Is.EquivalentTo(before.Where(e => e.Key != LibraryZip.InfoJsonEntry)));
    }

    [Test]
    public async Task SavesChange_AddsReplacesAndRemovesEntries()
    {
        var kept = Save([1]);
        var replaced = Save([2]);
        var removed = Save([3]);
        AddEntries((LibraryZip.SavesIndexEntry, GameSaves.Serialize([kept, replaced, removed])!), (kept.Entry, [1]), (replaced.Entry, [2]),
            (removed.Entry, [3]));
        var added = Save([4, 4], extension: ".srm");
        var replacement = replaced with { SizeBytes = 2, Sha1 = Convert.ToHexStringLower(SHA1.HashData(new byte[] { 7, 7 })), Extension = "" };
        var index = GameSaves.Serialize([kept, replacement, added])!;

        await Write(saves: new SavesChange(index, new Dictionary<string, Func<Stream>>
        {
            [added.Entry] = () => new MemoryStream([4, 4]),
            [replacement.Entry] = () => new MemoryStream([7, 7]),
        }, new HashSet<string> { removed.Entry, replaced.Entry }));

        var saves = SavesEntries();
        Assert.That(saves.Keys.Order(), Is.EqualTo(new[] { kept.Entry, replacement.Entry, added.Entry, LibraryZip.SavesIndexEntry }.Order()));
        Assert.That((saves[kept.Entry], saves[replacement.Entry], saves[added.Entry]), Is.EqualTo((new byte[] { 1 }, new byte[] { 7, 7 }, new byte[] { 4, 4 })));
        Assert.That(saves[LibraryZip.SavesIndexEntry], Is.EqualTo(index));
    }

    [Test]
    public async Task SavesChange_NullIndex_RemovesTheFolder()
    {
        var save = Save([1]);
        AddEntries((LibraryZip.SavesIndexEntry, GameSaves.Serialize([save])!), (save.Entry, [1]));

        await Write(saves: new SavesChange(null, new Dictionary<string, Func<Stream>>(), new HashSet<string> { save.Entry }));

        Assert.That(SavesEntries(), Is.Empty);
    }

    [Test]
    public async Task SavesChange_AnUnlistedAddedEntry_IsRejected()
    {
        var save = Save([1]);
        var change = new SavesChange(GameSaves.Serialize([save])!, new Dictionary<string, Func<Stream>>
        {
            [save.Entry] = () => new MemoryStream([1]),
            ["saves/stray.bin"] = () => new MemoryStream([2]),
        }, new HashSet<string>());

        await Assert.ThrowsAsync<ArgumentException>(() => Write(saves: change));
    }

    [Test]
    public async Task SavesChange_AnAddedEntryWithTheWrongSha1_FailsVerification_AndLeavesTheZip()
    {
        var save = Save([1, 2, 3]);
        var before = File.ReadAllBytes(_zip);

        var ex = await Assert.ThrowsAsync<LibraryZipVerifyException>(() => Write(saves: new SavesChange(GameSaves.Serialize([save])!,
            new Dictionary<string, Func<Stream>> { [save.Entry] = () => new MemoryStream([9, 9, 9]) }, new HashSet<string>())));

        Assert.That(ex!.EntryName, Is.EqualTo(save.Entry));
        Assert.That(File.ReadAllBytes(_zip), Is.EqualTo(before));
        Assert.That(File.Exists(_zip + ".tmp"), Is.False);
    }
}
