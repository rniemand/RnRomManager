using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Ingest;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Spec §7.3 archive rules: limits, traversal, symlinks, encryption, corruption, skipped entries; the 7z fixture.</summary>
public class ArchiveExtractorTests
{
    private const long MaxRom = 64 * 1024;

    private TempDir _dir = null!;
    private ArchiveExtractor _extractor = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _extractor = new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static long? RomLimit(string ext) => ext.Equals(".gb", StringComparison.OrdinalIgnoreCase) || ext == ".gbc" ? MaxRom : null;

    private string Work => _dir.Combine("work");

    private string Zip(params (string Name, byte[] Bytes)[] entries) => Zip(null, entries);

    private string Zip(Action<ZipArchiveEntry>? tweak, params (string Name, byte[] Bytes)[] entries)
    {
        var path = _dir.Combine($"a{Guid.NewGuid():N}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name);
                tweak?.Invoke(entry);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }
        return path;
    }

    private ArchiveRejectedException Rejected(string archive) =>
        Assert.Throws<ArchiveRejectedException>(() => _extractor.Extract(archive, Work, RomLimit))!;

    [Test]
    public void ExtractsRomEntries_KeepsSubfolderPaths_SkipsOtherFilesAndNestedArchives()
    {
        var a = new byte[1000];
        var b = new byte[2000];
        b[0] = 1;
        var archive = Zip(("Tetris.GB", a), ("sub/dir/Zelda.gbc", b), ("readme.txt", [1]), ("inner.zip", [1, 2]), ("other/Tetris.gb", b));

        var entries = _extractor.Extract(archive, Work, RomLimit);

        Assert.That(entries.Select(e => (e.EntryPath, e.FileName, e.SizeBytes)), Is.EqualTo(new[]
        {
            ("Tetris.GB", "Tetris.GB", 1000L),
            ("sub/dir/Zelda.gbc", "Zelda.gbc", 2000L),
            ("other/Tetris.gb", "Tetris.gb", 2000L),
        }));
        Assert.That(File.ReadAllBytes(entries[1].ExtractedPath), Is.EqualTo(b));
        Assert.That(entries[0].ExtractedPath, Is.Not.EqualTo(entries[2].ExtractedPath), "same file name, separate folders");
        Assert.That(entries.All(e => e.ExtractedPath.StartsWith(Work, StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void TooManyEntries_IsUnsafe()
    {
        var extractor = new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance) { MaxEntries = 3 };
        var archive = Zip(Enumerable.Range(0, 4).Select(i => ($"{i}.txt", new byte[1])).ToArray());

        var ex = Assert.Throws<ArchiveRejectedException>(() => extractor.Extract(archive, Work, RomLimit))!;

        Assert.That(ex.Reason, Is.EqualTo(ArchiveFailures.ArchiveUnsafe));
        Assert.That(ArchiveExtractor.DefaultMaxEntries, Is.EqualTo(100));
    }

    [TestCase("../evil.gb")]
    [TestCase("sub/../../evil.gb")]
    [TestCase("/abs/evil.gb")]
    [TestCase("..\\evil.gb")]
    public void TraversalOrAbsoluteEntry_IsUnsafe_AndNothingIsWritten(string name)
    {
        var archive = Zip(("good.gb", new byte[10]), (name, new byte[10]));

        Assert.That(Rejected(archive).Reason, Is.EqualTo(ArchiveFailures.ArchiveUnsafe));
        Assert.That(Directory.Exists(Work) ? Directory.EnumerateFiles(Work, "*", SearchOption.AllDirectories) : [], Is.Empty);
        Assert.That(File.Exists(_dir.Combine("evil.gb")), Is.False);
    }

    [Test]
    public void SymlinkEntry_IsUnsafe()
    {
        var archive = Zip(e => e.ExternalAttributes = unchecked((int)0xA1FF0000), ("link.gb", "../../etc/passwd"u8.ToArray()));

        Assert.That(Rejected(archive).Reason, Is.EqualTo(ArchiveFailures.ArchiveUnsafe));
    }

    [Test]
    public void EncryptedEntry_IsEncrypted()
    {
        var archive = Zip(("secret.gb", new byte[100]));
        SetEncryptedFlag(archive);

        Assert.That(Rejected(archive).Reason, Is.EqualTo(ArchiveFailures.ArchiveEncrypted));
    }

    [Test]
    public void NotAnArchive_IsCorrupt()
    {
        var path = _dir.Combine("junk.zip");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);

        Assert.That(Rejected(path).Reason, Is.EqualTo(ArchiveFailures.ArchiveCorrupt));
    }

    [Test]
    public void DamagedCompressedData_IsCorrupt()
    {
        var archive = Zip(("a.gb", Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray()));
        var bytes = File.ReadAllBytes(archive);
        for (var i = 40; i < 200; i++)
            bytes[i] ^= 0x5A; // inside the deflate stream, after the local header
        File.WriteAllBytes(archive, bytes);

        Assert.That(Rejected(archive).Reason, Is.EqualTo(ArchiveFailures.ArchiveCorrupt));
    }

    [Test]
    public void RomEntryOverTheHandlerMax_IsUnsafe_CountedOnActualBytes()
    {
        var archive = Zip(("big.gb", new byte[MaxRom + 1]));

        var ex = Rejected(archive);

        Assert.That(ex.Reason, Is.EqualTo(ArchiveFailures.ArchiveUnsafe));
        Assert.That(ex.Message, Does.Contain("big.gb"));
    }

    [Test]
    public void TotalOverTheLimit_IsUnsafe()
    {
        var extractor = new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance) { MaxTotalBytes = 1500 };
        var archive = Zip(("a.gb", new byte[1000]), ("b.gb", new byte[1000]));

        var ex = Assert.Throws<ArchiveRejectedException>(() => extractor.Extract(archive, Work, RomLimit))!;

        Assert.That(ex.Reason, Is.EqualTo(ArchiveFailures.ArchiveUnsafe));
        Assert.That(ex.Message, Does.Contain("in total"));
        Assert.That(ArchiveExtractor.DefaultMaxTotalBytes, Is.EqualTo(256L * 1024 * 1024));
    }

    [Test]
    public void SevenZipFixture_HoldsTheBuilderOutput()
    {
        var entries = _extractor.Extract(TestFixtures.Basic7zPath, Work, RomLimit);

        Assert.That(entries.Single().EntryPath, Is.EqualTo(TestFixtures.Basic7zEntry));
        Assert.That(File.ReadAllBytes(entries[0].ExtractedPath), Is.EqualTo(TestFixtures.Basic7zRom()));
    }

    [Test]
    public void SevenZipThatIsNot_IsCorrupt()
    {
        var path = _dir.Combine("junk.7z");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32]);

        Assert.That(Rejected(path).Reason, Is.EqualTo(ArchiveFailures.ArchiveCorrupt));
    }

    // --- external systems (spec I1): read from the caller's stream, numbered work paths, entry names as text only ---

    private static long? NspLimit(string ext) => ext.Equals(".nsp", StringComparison.OrdinalIgnoreCase) ? MaxRom : null;

    private IReadOnlyList<ExtractedEntry> ExtractExternal(string archive, ArchiveExtractor? extractor = null)
    {
        using var stream = File.OpenRead(archive);
        return (extractor ?? _extractor).Extract(stream, Path.GetFileName(archive), Work, NspLimit);
    }

    private string[] WorkFiles() => Directory.Exists(Work) ? Directory.GetFiles(Work, "*", SearchOption.AllDirectories) : [];

    [Test]
    public void External_WritesNumberedFiles_AndKeepsTheEntryNameAsText()
    {
        // A name Windows can't store, upper-case extension, next to a skipped file.
        var archive = Zip(("readme.txt", [9]), ("dir/Bad<Name>:What?.NSP", [1, 2, 3]), ("b.nsp", [4]));

        var entries = ExtractExternal(archive);

        Assert.That(entries.Select(e => (e.EntryPath, e.FileName, e.SizeBytes)),
            Is.EqualTo(new[] { ("dir/Bad<Name>:What?.NSP", "Bad<Name>:What?.NSP", 3L), ("b.nsp", "b.nsp", 1L) }));
        Assert.That(entries.Select(e => e.ExtractedPath),
            Is.EqualTo(new[] { Path.Combine(Work, "1", "entry.nsp"), Path.Combine(Work, "2", "entry.nsp") }));
        Assert.That(File.ReadAllBytes(entries[0].ExtractedPath), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void External_EntryPathOf512Scalars_IsAccepted_513IsRejectedBeforeAnythingIsWritten()
    {
        var folders = string.Concat(Enumerable.Repeat(new string('d', 99) + "/", 5)); // 500
        var ok = folders + new string('n', 8) + ".nsp"; // 512
        Assert.That(ok.Length, Is.EqualTo(ArchiveExtractor.MaxEntryPathScalars));

        Assert.That(ExtractExternal(Zip((ok, [1]))).Single().EntryPath, Is.EqualTo(ok));
        Directory.Delete(Work, recursive: true);

        var rejected = Assert.Throws<ArchiveRejectedException>(() => ExtractExternal(Zip(("first.nsp", [1]), ("n" + ok, [2]))))!;
        Assert.That(rejected.Reason, Is.EqualTo(ArchiveFailures.ArchiveUnsafe));
        Assert.That(WorkFiles(), Is.Empty);
    }

    [Test]
    public void External_OverlongBaseName_IsRejected_ButOnlyForRomEntries()
    {
        var name = new string('n', 252); // + ".nsp" = 256
        var rejected = Assert.Throws<ArchiveRejectedException>(() => ExtractExternal(Zip((name + ".nsp", [1]))))!;
        Assert.That(rejected.Reason, Is.EqualTo(ArchiveFailures.ArchiveUnsafe));

        Assert.That(ExtractExternal(Zip((name + ".txt", [1]), ("a.nsp", [2]))).Single().EntryPath, Is.EqualTo("a.nsp"));
    }

    [Test]
    public void External_SamePathUnderBothSeparators_IsRejectedBeforeExtraction_CaseVariantsAreDistinct()
    {
        var rejected = Assert.Throws<ArchiveRejectedException>(() => ExtractExternal(Zip(("nested/a.nsp", [1]), ("nested\\a.nsp", [2]))))!;
        Assert.That(rejected.Reason, Is.EqualTo(ArchiveFailures.ArchiveUnsafe));
        Assert.That(WorkFiles(), Is.Empty);

        var entries = ExtractExternal(Zip(("nested/a.nsp", [1]), ("nested/A.nsp", [2]), ("other/a.nsp", [3])));
        Assert.That(entries.Select(e => e.EntryPath), Is.EqualTo(new[] { "nested/a.nsp", "nested/A.nsp", "other/a.nsp" }));
        Assert.That(entries.Select(e => File.ReadAllBytes(e.ExtractedPath)[0]), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [TestCase("/abs.nsp")]
    [TestCase("C:/abs.nsp")]
    [TestCase("c:abs.nsp")]
    [TestCase("a/../b.nsp")]
    public void External_RootedOrTraversingEntry_IsUnsafeOnEveryOs(string entry)
    {
        var rejected = Assert.Throws<ArchiveRejectedException>(() => ExtractExternal(Zip((entry, [1]))))!;

        Assert.That(rejected.Reason, Is.EqualTo(ArchiveFailures.ArchiveUnsafe));
        Assert.That(WorkFiles(), Is.Empty);
    }

    [Test]
    public void External_FailedOutputWrite_IsAnOutputError_NotACorruptArchive()
    {
        var failing = new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance) { CreateOutput = _ => throw new IOException("disk full") };

        var ex = Assert.Throws<ArchiveOutputException>(() => ExtractExternal(Zip(("a.nsp", [1])), failing))!;

        Assert.That(ex.Message, Does.Contain("disk full"));
    }

    [Test]
    public void External_NeverOverwritesAnExtractedCopy()
    {
        Directory.CreateDirectory(Path.Combine(Work, "0"));
        File.WriteAllText(Path.Combine(Work, "0", "entry.nsp"), "occupant");

        Assert.Throws<ArchiveOutputException>(() => ExtractExternal(Zip(("a.nsp", [1]))));

        Assert.That(File.ReadAllText(Path.Combine(Work, "0", "entry.nsp")), Is.EqualTo("occupant"));
    }

    [Test]
    public void External_CorruptBytes_AreStillArchiveCorrupt_AndOversizeStillUnsafe()
    {
        var junk = _dir.Combine("junk.zip");
        File.WriteAllBytes(junk, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.That(Assert.Throws<ArchiveRejectedException>(() => ExtractExternal(junk))!.Reason, Is.EqualTo(ArchiveFailures.ArchiveCorrupt));

        var big = Zip(("big.nsp", new byte[MaxRom + 1]));
        Assert.That(Assert.Throws<ArchiveRejectedException>(() => ExtractExternal(big))!.Reason, Is.EqualTo(ArchiveFailures.ArchiveUnsafe));
    }

    [Test]
    public void External_SevenZip_IsReadFromTheStream()
    {
        using var stream = File.OpenRead(TestFixtures.Basic7zPath);

        var entries = _extractor.Extract(stream, "pack.7z", Work, ext => ext == Path.GetExtension(TestFixtures.Basic7zEntry) ? MaxRom : null);

        Assert.That(entries.Single().EntryPath, Is.EqualTo(TestFixtures.Basic7zEntry));
        Assert.That(File.ReadAllBytes(entries[0].ExtractedPath), Is.EqualTo(TestFixtures.Basic7zRom()));
        Assert.That(stream.CanRead, Is.True, "the caller's stream stays open");
    }

    // --- the checked work directory the extraction goes into ---

    [Test]
    public void WorkDirectory_IsCreatedWithItsParents_AndAnOccupantIsNeverAdopted()
    {
        var root = _dir.Combine("cache");
        var work = Path.Combine(root, "work", "job", "archive");

        var created = RnRomManager.Core.Storage.ExternalRomPath.CreateDirectory(root, work);
        Assert.That((created.Status, created.Path), Is.EqualTo((RnRomManager.Core.Storage.ExternalPathStatus.Found, work)));

        File.WriteAllText(Path.Combine(work, "theirs.bin"), "occupant");
        var again = RnRomManager.Core.Storage.ExternalRomPath.CreateDirectory(root, work);
        Assert.That(again.Status, Is.EqualTo(RnRomManager.Core.Storage.ExternalPathStatus.Unsafe));
        Assert.That(File.ReadAllText(Path.Combine(work, "theirs.bin")), Is.EqualTo("occupant"));
    }

    [Test]
    public void WorkDirectory_BehindALinkedBase_IsRefused()
    {
        var root = _dir.Combine("cache");
        var outside = Directory.CreateDirectory(_dir.Combine("outside")).FullName;
        Directory.CreateDirectory(root);
        var link = Path.Combine(root, "work");
        Assert.That(TestLinks.TryLinkDirectory(link, outside), Is.True, "this account can't create a directory link");
        try
        {
            var created = RnRomManager.Core.Storage.ExternalRomPath.CreateDirectory(root, Path.Combine(link, "job", "archive"));

            Assert.That(created.Status, Is.EqualTo(RnRomManager.Core.Storage.ExternalPathStatus.Unsafe));
            Assert.That(Directory.GetFileSystemEntries(outside), Is.Empty);
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(link);
        }
    }

    /// <summary>Sets general-purpose flag bit 0 (encrypted) in every local and central header; .NET can't write encrypted ZIPs.</summary>
    private static void SetEncryptedFlag(string path)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i < bytes.Length - 4; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04)
                bytes[i + 6] |= 1;
            else if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
                bytes[i + 8] |= 1;
        }
        File.WriteAllBytes(path, bytes);
    }
}
