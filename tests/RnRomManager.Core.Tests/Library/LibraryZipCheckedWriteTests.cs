using System.Security.Cryptography;
using NSubstitute;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Spec W1: the checked ZIP write used for external games (no ROM entries, verified sidecars, guarded publication).</summary>
public class LibraryZipCheckedWriteTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, Func<Stream>> NoEntries = new Dictionary<string, Func<Stream>>();

    private TempDir _dir = null!;
    private string _root = null!;
    private string _folder = null!;
    private string _target = null!;
    private InfoJsonReader _reader = null!;
    private LibraryZipWriter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _root = _dir.Combine("roms");
        _folder = Path.Combine(_root, "ext", "O");
        Directory.CreateDirectory(_folder);
        _target = Path.Combine(_folder, "Orbit.zip");
        var gb = Substitute.For<ISystemHandler>();
        gb.EmbedRomsInZip.Returns(true);
        gb.Id.Returns("gb");
        ISystemHandler[] handlers = [gb, new ExternalTestHandler()];
        var registry = Substitute.For<ISystemRegistry>();
        registry.All.Returns(handlers);
        _reader = new InfoJsonReader([], registry);
        _writer = new LibraryZipWriter(_reader);
    }

    [TearDown]
    public void TearDown()
    {
        var link = Path.Combine(_root, "ext");
        if (Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null)
            TestLinks.RemoveDirectoryLink(link);
        _dir.Dispose();
    }

    private static byte[] Bytes(int seed, int length = 64) => [.. Enumerable.Range(0, length).Select(i => (byte)(seed * 31 + i))];

    private static InfoJsonRom Rom(string fileName, byte[] bytes, string? pointer, bool isPrimary = true) => new()
    {
        FileName = fileName,
        ExternalFileName = pointer,
        OriginalFileName = fileName,
        BaseTitle = "Orbit",
        Regions = [],
        Languages = [],
        Revision = null,
        Flags = [],
        IsPrimary = isPrimary,
        Identification = new InfoJsonIdentification(IdentificationMethod.Manual),
        Header = null,
        Metadata = new InfoJsonRomMetadata(bytes.Length, Crc32.ToHex(bytes), Convert.ToHexStringLower(MD5.HashData(bytes)),
            Convert.ToHexStringLower(SHA1.HashData(bytes)), When, When, "/app/ingest/ext/" + fileName),
    };

    private static InfoJsonGame Game(string system, params InfoJsonRom[] roms) => new()
    {
        SchemaVersion = InfoJsonReader.CurrentVersion,
        GameId = Guid.Parse("6f1f6c3e-8a54-4f39-9d2b-0d2a7c1e9b11"),
        System = system,
        Title = "Orbit",
        DisplayTitle = "Orbit",
        ReleaseYear = null,
        Developers = [],
        Publishers = [],
        Genres = [],
        Description = null,
        Rating = null,
        Favourite = false,
        Roms = roms,
        Images = [],
        Metadata = new InfoJsonGameMetadata(When, When, "0.1.0"),
    };

    private const string RomName = "Orbit [v0].nsp";
    private const string Pointer = "Orbit.Orbit [v0].nsp";
    private static readonly byte[] RomBytes = Bytes(1);

    private static InfoJsonGame External() => Game("ext", Rom(RomName, RomBytes, Pointer));

    /// <summary>Puts an external ROM file next to the ZIP, as a move would, and returns its promotion snapshot.</summary>
    private ExternalFileSnapshot Place(string name, byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(_folder, name), bytes);
        return SnapshotOf(Path.Combine(_folder, name));
    }

    private ExternalFileSnapshot SnapshotOf(string path) => ExternalRomPath.ClassifyFile(_root, path).Snapshot!;

    private static LibraryZipChanges Changes(InfoJsonGame game, IReadOnlyDictionary<string, ExternalFileSnapshot>? externalFiles = null,
        IReadOnlyDictionary<string, Func<Stream>>? entries = null) => new(game, entries ?? NoEntries, null, externalFiles);

    private static Dictionary<string, ExternalFileSnapshot> Touched(string name, ExternalFileSnapshot snapshot) =>
        new(StringComparer.OrdinalIgnoreCase) { [name] = snapshot };

    private Task<ExternalFileSnapshot> WriteAsync(LibraryZip? existing, LibraryZipChanges changes, LibraryZipWriterHooks? hooks = null) =>
        _writer.WriteCheckedAsync(_root, _target, existing, changes, default, hooks);

    private string[] Left() => [.. Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    // --- content ---

    [Test]
    public async Task ExternalGame_HasNoRomsEntry_AndTheTouchedFileIsVerified()
    {
        var placed = Place(Pointer, RomBytes);

        var published = await WriteAsync(null, Changes(External(), Touched(Pointer, placed)));

        using var zip = LibraryZip.Open(_target, _reader);
        Assert.That(zip.EntryNames, Is.EqualTo(new[] { "info.json" }));
        Assert.That(zip.InfoJson!.Game!.Roms.Single().ExternalFileName, Is.EqualTo(Pointer));
        Assert.That((published, zip.Snapshot), Is.EqualTo((SnapshotOf(_target), SnapshotOf(_target))), "the returned snapshot is the published file");
        Assert.That(Left(), Is.EqualTo(new[] { Pointer, "Orbit.zip" }), "no temp left");
    }

    [Test]
    public async Task RomsEntryForAnExternalRom_IsRejected()
    {
        var entries = new Dictionary<string, Func<Stream>> { ["roms/" + RomName] = () => new MemoryStream(RomBytes) };

        await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(null, Changes(External(), entries: entries)));
        Assert.That(Left(), Is.Empty);
    }

    [Test]
    public async Task ExternalFileThatIsNotListed_IsRejected()
    {
        var placed = Place("Other.nsp", RomBytes);

        await Assert.ThrowsAsync<ArgumentException>(() => WriteAsync(null, Changes(External(), Touched("Other.nsp", placed))));
    }

    [Test]
    public async Task TouchedFileMissing_VerifyFailed_NothingPublished()
    {
        var placed = Place(Pointer, RomBytes);
        File.Delete(Path.Combine(_folder, Pointer));

        var ex = await Assert.ThrowsAsync<LibraryZipVerifyException>(() => WriteAsync(null, Changes(External(), Touched(Pointer, placed))));

        Assert.That(ex!.EntryName, Is.EqualTo(Pointer));
        Assert.That(Left(), Is.Empty);
    }

    [Test]
    public async Task TouchedFileOfTheWrongLength_VerifyFailed_AndTheFileStays()
    {
        var placed = Place(Pointer, Bytes(1, 60)); // info.json records 64 bytes

        var ex = await Assert.ThrowsAsync<LibraryZipVerifyException>(() => WriteAsync(null, Changes(External(), Touched(Pointer, placed))));

        Assert.That(ex!.Message, Does.Contain("size 60 != info.json 64"));
        Assert.That(Left(), Is.EqualTo(new[] { Pointer }));
    }

    [Test]
    public async Task SameSizeReplacementOfTheTouchedFile_VerifyFailed_AndTheReplacementIsUntouched()
    {
        var placed = Place(Pointer, RomBytes);
        var path = Path.Combine(_folder, Pointer);
        File.Move(path, Path.Combine(_dir.Path, "promoted-away"));
        File.WriteAllBytes(path, Bytes(2)); // same name, same length, another file
        File.SetLastWriteTimeUtc(path, placed.LastWriteTimeUtc);

        var ex = await Assert.ThrowsAsync<LibraryZipVerifyException>(() => WriteAsync(null, Changes(External(), Touched(Pointer, placed))));

        Assert.That(ex!.Message, Does.Contain("not the file this write placed"));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(Bytes(2)));
        Assert.That(Left(), Is.EqualTo(new[] { Pointer }));
    }

    [Test]
    public async Task TouchedFileReplacedRightBeforePublication_VerifyFailed()
    {
        var placed = Place(Pointer, RomBytes);
        var hooks = new LibraryZipWriterHooks { BeforePublicationAttempt = _ => File.AppendAllText(Path.Combine(_folder, Pointer), "x") };

        await Assert.ThrowsAsync<LibraryZipVerifyException>(() => WriteAsync(null, Changes(External(), Touched(Pointer, placed)), hooks));
        Assert.That(Left(), Is.EqualTo(new[] { Pointer }));
    }

    [Test]
    public async Task ExternalFileTheWriteDidNotTouch_MayBeMissingOrWrong()
    {
        var game = Game("ext", Rom(RomName, RomBytes, Pointer), Rom("Orbit [v1].nsp", Bytes(2), "Orbit.Orbit [v1].nsp", isPrimary: false));
        Place("Orbit.Orbit [v1].nsp", Bytes(2, 10)); // wrong length; the first file is missing altogether

        await WriteAsync(null, Changes(game));

        using var zip = LibraryZip.Open(_target, _reader);
        Assert.That(zip.InfoJson!.Game!.Roms.Select(r => r.ExternalFileName), Is.EqualTo(new[] { Pointer, "Orbit.Orbit [v1].nsp" }));
    }

    [Test]
    public async Task Notes_AreWrittenAndKeptOnARebuild()
    {
        var note = LibraryZip.NoteEntry(RomName);
        await WriteAsync(null, Changes(External(), entries: new Dictionary<string, Func<Stream>> { [note] = () => new MemoryStream("# Hi"u8.ToArray()) }));

        using (var existing = LibraryZip.Open(_target, _reader))
            await WriteAsync(existing, Changes(External() with { Title = "Orbit!" }));

        using var zip = LibraryZip.Open(_target, _reader);
        Assert.That(zip.EntryNames, Is.EquivalentTo(new[] { "info.json", note }));
        Assert.That(zip.InfoJson!.Game!.Title, Is.EqualTo("Orbit!"));
        using var reader = new StreamReader(zip.OpenEntry(note, 100));
        Assert.That(reader.ReadToEnd(), Is.EqualTo("# Hi"));
    }

    [Test]
    public async Task EmbeddedGame_CanUseTheCheckedWrite()
    {
        Directory.CreateDirectory(Path.Combine(_root, "gb", "O"));
        var target = Path.Combine(_root, "gb", "O", "Orbit.zip");
        var game = Game("gb", Rom("Orbit.gb", RomBytes, null));

        await _writer.WriteCheckedAsync(_root, target, null, Changes(game, entries: new Dictionary<string, Func<Stream>> { ["roms/Orbit.gb"] = () => new MemoryStream(RomBytes) }));

        using var zip = LibraryZip.Open(target, _reader);
        Assert.That(zip.EntryNames, Is.EquivalentTo(new[] { "info.json", "roms/Orbit.gb" }));
    }

    // --- the temp file ---

    [Test]
    public async Task OccupiedTemp_IsIoError_AndTheOccupantIsPreserved()
    {
        File.WriteAllText(_target + ".tmp", "someone else's temp");

        await Assert.ThrowsAsync<IOException>(() => WriteAsync(null, Changes(External())));

        Assert.That(File.ReadAllText(_target + ".tmp"), Is.EqualTo("someone else's temp"));
        Assert.That(Left(), Is.EqualTo(new[] { "Orbit.zip.tmp" }));
    }

    [Test]
    public async Task TempReplacedAfterItWasClosed_IsNeitherPublishedNorCleanedUp()
    {
        var hooks = new LibraryZipWriterHooks
        {
            AfterTempClosed = () =>
            {
                File.Move(_target + ".tmp", Path.Combine(_dir.Path, "our-temp"));
                File.WriteAllText(_target + ".tmp", "a replacement");
            },
        };

        await Assert.ThrowsAsync<IOException>(() => WriteAsync(null, Changes(External()), hooks));

        Assert.That(File.ReadAllText(_target + ".tmp"), Is.EqualTo("a replacement"));
        Assert.That(Left(), Is.EqualTo(new[] { "Orbit.zip.tmp" }), "nothing was published");
    }

    [Test]
    public async Task TempReplacedAfterVerification_IsNeitherPublishedNorCleanedUp()
    {
        var hooks = new LibraryZipWriterHooks
        {
            BeforePublicationAttempt = _ =>
            {
                File.Move(_target + ".tmp", Path.Combine(_dir.Path, "our-temp"));
                File.WriteAllText(_target + ".tmp", "a replacement");
            },
        };

        await Assert.ThrowsAsync<IOException>(() => WriteAsync(null, Changes(External()), hooks));

        Assert.That(File.ReadAllText(_target + ".tmp"), Is.EqualTo("a replacement"));
        Assert.That(Left(), Is.EqualTo(new[] { "Orbit.zip.tmp" }));
    }

    // --- publishing a new path ---

    [Test]
    public async Task NewPath_AlreadyTaken_IsIoError_BeforeAnythingIsWritten()
    {
        File.WriteAllText(_target, "occupant");

        await Assert.ThrowsAsync<IOException>(() => WriteAsync(null, Changes(External())));

        Assert.That(File.ReadAllText(_target), Is.EqualTo("occupant"));
        Assert.That(Left(), Is.EqualTo(new[] { "Orbit.zip" }));
    }

    [Test]
    public async Task NewPath_TakenJustBeforePublication_IsIoError_AndTheOccupantIsPreserved()
    {
        var hooks = new LibraryZipWriterHooks { BeforePublicationAttempt = _ => File.WriteAllText(_target, "occupant") };

        await Assert.ThrowsAsync<IOException>(() => WriteAsync(null, Changes(External()), hooks));

        Assert.That(File.ReadAllText(_target), Is.EqualTo("occupant"));
        Assert.That(Left(), Is.EqualTo(new[] { "Orbit.zip" }), "our temp is cleaned up");
    }

    [Test]
    public async Task NewPath_TakenInAnotherLetterCase_IsIoError()
    {
        var hooks = new LibraryZipWriterHooks { BeforePublicationAttempt = _ => File.WriteAllText(Path.Combine(_folder, "ORBIT.ZIP"), "occupant") };

        await Assert.ThrowsAsync<IOException>(() => WriteAsync(null, Changes(External()), hooks));

        Assert.That(Left(), Is.EqualTo(new[] { "ORBIT.ZIP" }));
        Assert.That(File.ReadAllText(Path.Combine(_folder, "ORBIT.ZIP")), Is.EqualTo("occupant"));
    }

    [Test]
    public async Task PathChange_PublishesTheNewPath_AndLeavesTheOldZip()
    {
        await WriteAsync(null, Changes(External()));
        var renamed = Path.Combine(_folder, "Orbit Two.zip");

        using (var existing = LibraryZip.Open(_target, _reader))
            await _writer.WriteCheckedAsync(_root, renamed, existing, Changes(External() with { Title = "Orbit Two" }));

        Assert.That(Left(), Is.EqualTo(new[] { "Orbit Two.zip", "Orbit.zip" }));
        using var zip = LibraryZip.Open(renamed, _reader);
        Assert.That(zip.InfoJson!.Game!.Title, Is.EqualTo("Orbit Two"));
    }

    [Test]
    public async Task Publication_IsRetriedAfterASharingFailure()
    {
        var attempts = new List<int>();
        var hooks = new LibraryZipWriterHooks { BeforePublicationAttempt = attempts.Add, FailPublicationAttempt = attempt => attempt == 0 };

        await WriteAsync(null, Changes(External()), hooks);

        Assert.That(attempts, Is.EqualTo(new[] { 0, 1 }));
        Assert.That(Left(), Is.EqualTo(new[] { "Orbit.zip" }));
    }

    // --- rebuilding at the current path ---

    private LibraryZipWriterHooks InstallAnotherZipBeforeAttempt(int attempt, bool failFirst = false) => new()
    {
        FailPublicationAttempt = a => failFirst && a == 0,
        BeforePublicationAttempt = a =>
        {
            if (a != attempt)
                return;
            File.Move(_target, Path.Combine(_dir.Path, "original.zip"));
            File.WriteAllText(_target, "a different ZIP");
        },
    };

    [Test]
    public async Task Rebuild_PreservesADifferentZipInstalledAfterTheOriginalWasOpened()
    {
        await WriteAsync(null, Changes(External()));

        using (var existing = LibraryZip.Open(_target, _reader))
            await Assert.ThrowsAsync<IOException>(() => WriteAsync(existing, Changes(External() with { Title = "Changed" }), InstallAnotherZipBeforeAttempt(0)));

        Assert.That(File.ReadAllText(_target), Is.EqualTo("a different ZIP"));
        Assert.That(Left(), Is.EqualTo(new[] { "Orbit.zip" }));
    }

    [Test]
    public async Task Rebuild_PreservesADifferentZipInstalledBetweenTwoRetries()
    {
        await WriteAsync(null, Changes(External()));

        using (var existing = LibraryZip.Open(_target, _reader))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                WriteAsync(existing, Changes(External() with { Title = "Changed" }), InstallAnotherZipBeforeAttempt(1, failFirst: true)));
        }

        Assert.That(File.ReadAllText(_target), Is.EqualTo("a different ZIP"));
        Assert.That(Left(), Is.EqualTo(new[] { "Orbit.zip" }));
    }

    [Test]
    public async Task Rebuild_WithTheSameSizeAndSecond_AdvancesTheStamp_SoASyncSeesIt()
    {
        await WriteAsync(null, Changes(External()));
        // A stamp the rewrite can't beat by itself: the same bytes, and "now" is earlier than this.
        var stamp = MirrorEntry.WholeSeconds(DateTime.UtcNow.AddHours(1));
        File.SetLastWriteTimeUtc(_target, stamp);
        var before = new FileInfo(_target).Length;

        ExternalFileSnapshot published;
        using (var existing = LibraryZip.Open(_target, _reader))
            published = await WriteAsync(existing, Changes(External()));

        Assert.That(new FileInfo(_target).Length, Is.EqualTo(before), "the test needs an unchanged size");
        Assert.That(File.GetLastWriteTimeUtc(_target), Is.EqualTo(stamp.AddSeconds(1)));
        Assert.That(published, Is.EqualTo(SnapshotOf(_target)), "the returned snapshot carries the advanced stamp");
    }

    [Test]
    public async Task Rebuild_WithADifferentSize_KeepsItsOwnStamp()
    {
        await WriteAsync(null, Changes(External()));
        var stamp = MirrorEntry.WholeSeconds(DateTime.UtcNow.AddHours(1));
        File.SetLastWriteTimeUtc(_target, stamp);

        using (var existing = LibraryZip.Open(_target, _reader))
            await WriteAsync(existing, Changes(External() with { Description = new string('d', 500) }));

        Assert.That(File.GetLastWriteTimeUtc(_target), Is.LessThan(stamp));
    }

    [Test]
    public async Task EmbeddedPlainWrite_KeepsItsCurrentBehaviour_NoAdvancedStamp()
    {
        Directory.CreateDirectory(Path.Combine(_root, "gb", "O"));
        var target = Path.Combine(_root, "gb", "O", "Orbit.zip");
        var changes = Changes(Game("gb", Rom("Orbit.gb", RomBytes, null)), entries: new Dictionary<string, Func<Stream>> { ["roms/Orbit.gb"] = () => new MemoryStream(RomBytes) });
        await _writer.WriteAsync(target, null, changes);
        var stamp = MirrorEntry.WholeSeconds(DateTime.UtcNow.AddHours(1));
        File.SetLastWriteTimeUtc(target, stamp);
        var before = new FileInfo(target).Length;

        using (var existing = LibraryZip.Open(target, _reader))
            await _writer.WriteAsync(target, existing, Changes(changes.Info));

        Assert.That(new FileInfo(target).Length, Is.EqualTo(before));
        Assert.That(File.GetLastWriteTimeUtc(target), Is.LessThan(stamp));
    }

    // --- the path below the library root ---

    [Test]
    public async Task LinkedAncestor_AboveTheDestinationFolder_IsIoError_AndNothingIsWrittenOutside()
    {
        var outside = _dir.Combine("outside");
        Directory.CreateDirectory(Path.Combine(outside, "O"));
        Directory.Delete(Path.Combine(_root, "ext"), recursive: true);
        if (!TestLinks.TryLinkDirectory(Path.Combine(_root, "ext"), outside))
            Assert.Ignore("can't create a directory link here");

        await Assert.ThrowsAsync<IOException>(() => WriteAsync(null, Changes(External())));

        Assert.That(Directory.GetFileSystemEntries(Path.Combine(outside, "O")), Is.Empty);
    }

    [Test]
    public async Task MissingDestinationFolder_OrATargetOutsideTheRoot_IsIoError()
    {
        var elsewhere = _dir.Combine("elsewhere");
        Directory.CreateDirectory(elsewhere);

        await Assert.ThrowsAsync<IOException>(() => _writer.WriteCheckedAsync(_root, Path.Combine(_root, "ext", "Q", "Orbit.zip"), null, Changes(External())));
        await Assert.ThrowsAsync<IOException>(() => _writer.WriteCheckedAsync(_root, Path.Combine(elsewhere, "Orbit.zip"), null, Changes(External())));
        Assert.That(Directory.GetFileSystemEntries(elsewhere), Is.Empty);
    }
}
