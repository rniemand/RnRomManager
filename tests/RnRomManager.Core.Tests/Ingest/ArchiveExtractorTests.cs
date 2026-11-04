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
