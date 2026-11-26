using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Configuration;
using NSubstitute;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Storage;

public class StorageFoldersTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("rnrm-storage-").FullName;

    [TearDown]
    public void TearDown()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(_root, recursive: true);
    }

    private PathsOptions Paths() => new()
    {
        Roms = Path.Combine(_root, "roms"),
        Ingest = Path.Combine(_root, "ingest"),
        Logs = Path.Combine(_root, "nested", "logs"),
        Cache = Path.Combine(_root, "cache"),
        Backups = Path.Combine(_root, "backups"),
        Collections = Path.Combine(_root, "collections"),
        Bios = Path.Combine(_root, "bios"),
        ImageCache = Path.Combine(_root, "imagecache"),
    };

    [Test]
    public void EnsureRoots_CreatesMissingFolders_AndLeavesNoProbe()
    {
        var paths = Paths();

        StorageFolders.EnsureRoots(paths);

        foreach (var (_, path) in paths.All())
        {
            Assert.That(Directory.Exists(path), Is.True, path);
            Assert.That(File.Exists(Path.Combine(path, StorageFolders.ProbeFileName)), Is.False, path);
        }
    }

    [Test]
    public void EnsureRoots_IsIdempotent_AndKeepsExistingFiles()
    {
        var paths = Paths();
        StorageFolders.EnsureRoots(paths);
        File.WriteAllText(Path.Combine(paths.Roms, "keep.txt"), "x");

        StorageFolders.EnsureRoots(paths);

        Assert.That(File.Exists(Path.Combine(paths.Roms, "keep.txt")), Is.True);
    }

    [Test]
    public void EnsureRoots_PathIsAFile_ThrowsNamingKeyAndPath()
    {
        var paths = Paths();
        File.WriteAllText(paths.Cache, "not a folder");

        var ex = Assert.Throws<StorageNotWritableException>(() => StorageFolders.EnsureRoots(paths));

        Assert.That(ex!.Message, Does.StartWith($"Paths:Cache '{paths.Cache}' is not writable by "));
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public void EnsureRoots_ReadOnlyFolder_ThrowsNamingPathAndUid()
    {
        Assume.That(OperatingSystem.IsWindows(), Is.False, "Read-only directory permissions are a Unix test.");
        Assume.That(ProcessIdentity.IsRoot(), Is.False, "root ignores directory permissions.");
        var paths = Paths();
        Directory.CreateDirectory(paths.Ingest);
        File.SetUnixFileMode(paths.Ingest, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var ex = Assert.Throws<StorageNotWritableException>(() => StorageFolders.EnsureRoots(paths));

        Assert.That(ex!.Message, Does.StartWith($"Paths:Ingest '{paths.Ingest}' is not writable by UID "));
        Assert.That(ex.Message, Does.Contain("/ GID "));
    }

    private static ISystemHandler Handler(string id, bool embed = true)
    {
        var handler = Substitute.For<ISystemHandler>();
        handler.EmbedRomsInZip.Returns(embed);
        handler.Id.Returns(id);
        return handler;
    }

    [Test]
    public void EnsureSystemFolders_CreatesIngestFoldersPerSystem_AndNoLibraryFolders()
    {
        var paths = Paths();

        StorageFolders.EnsureSystemFolders(paths, [Handler("gb"), Handler("gbc"), Handler("ext", embed: false)], NullLogger.Instance);

        Assert.That(Directory.Exists(paths.Roms), Is.False, "B30: roms/<system> is created on first write");
        foreach (var id in new[] { "gb", "gbc", "ext" })
        {
            Assert.That(Directory.GetDirectories(Path.Combine(paths.Ingest, id)).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "_duplicates", "_failed", "_removed" }));
        }
    }

    [Test]
    public void EnsureSystemFolders_IsIdempotent_AndKeepsExistingFiles()
    {
        var paths = Paths();
        StorageFolders.EnsureSystemFolders(paths, [Handler("gb")], NullLogger.Instance);
        var keep = Path.Combine(paths.Ingest, "gb", "_failed", "Tetris.zip");
        File.WriteAllText(keep, "x");

        StorageFolders.EnsureSystemFolders(paths, [Handler("gb")], NullLogger.Instance);

        Assert.That(File.ReadAllText(keep), Is.EqualTo("x"));
    }

    [Test]
    public void EnsureSystemFolders_TraversingHandlerId_IsRefused()
    {
        Assert.Throws<UnsafePathException>(() => StorageFolders.EnsureSystemFolders(Paths(), [Handler("../escape")], NullLogger.Instance));
    }

    [Test]
    public void EnsureSystemFolders_BlockedByAFile_ThrowsNamingTheFolder()
    {
        var paths = Paths();
        Directory.CreateDirectory(Path.Combine(paths.Ingest, "gb"));
        File.WriteAllText(Path.Combine(paths.Ingest, "gb", "_failed"), "file where a folder should be");

        var ex = Assert.Throws<StorageNotWritableException>(() => StorageFolders.EnsureSystemFolders(paths, [Handler("gb")], NullLogger.Instance));

        Assert.That(ex!.Message, Does.Contain(Path.Combine(paths.Ingest, "gb", "_failed")));
    }

    private static readonly string[] AllBuckets = ["#", .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString())];

    /// <summary>Links <paramref name="link"/> (its parent is created) to a new empty folder outside both roots.</summary>
    private string LinkOutside(string link)
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside", Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TestLinks.TryLinkDirectory(link, outside))
            Assert.Ignore("this account can't create a directory link");
        return outside;
    }

    [TestCase("ext", false, TestName = "EnsureSystemFolders_LinkedExternalFolder_IsSkipped ingest system folder")]
    [TestCase("ext/_failed", false, TestName = "EnsureSystemFolders_LinkedExternalFolder_IsSkipped side folder")]
    [TestCase("ext", true, TestName = "EnsureSystemFolders_LinkedExternalFolder_IsSkipped LegacyEmbeddedRoms set to embed")]
    public void EnsureSystemFolders_LinkedExternalFolder_IsSkipped(string linked, bool legacyEmbedded)
    {
        // B5a: a LegacyEmbeddedRoms system's folders get the checked creation whatever its EmbedRomsInZip.
        var handler = Handler("ext", embed: legacyEmbedded);
        handler.LegacyEmbeddedRoms.Returns(legacyEmbedded);
        var paths = Paths();
        var root = paths.Ingest;
        var link = Path.Combine(root, linked);
        var outside = LinkOutside(link);
        var logger = Substitute.For<ILogger>();
        try
        {
            StorageFolders.EnsureSystemFolders(paths, [handler], logger);

            Assert.That(Directory.GetFileSystemEntries(outside), Is.Empty, "nothing is created through the link");
            Assert.That(logger.ReceivedCalls().Count(c => c.GetArguments()[0] is LogLevel.Warning), Is.EqualTo(1));
            // The link's siblings are still created.
            if (linked.Contains('/'))
                Assert.That(Directory.Exists(Path.Combine(root, "ext", "_removed")), Is.True);
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(link);
        }
    }

    private static string[] Entries(string folder) =>
        Directory.EnumerateFileSystemEntries(folder).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    private static int Warnings(ILogger logger) => logger.ReceivedCalls().Count(c => c.GetArguments()[0] is LogLevel.Warning);

    [TestCase(true)]
    [TestCase(false)]
    public void RemoveEmptyLibraryFolders_RemovesEmptyBuckets_ThenTheEmptySystemFolder(bool embed)
    {
        var paths = Paths();
        foreach (var bucket in AllBuckets)
            Directory.CreateDirectory(Path.Combine(paths.Roms, "gb", bucket));
        Directory.CreateDirectory(Path.Combine(paths.Roms, "nes", "A"));
        File.WriteAllText(Path.Combine(paths.Roms, "nes", "A", "Adventure.zip"), "x");
        Directory.CreateDirectory(Path.Combine(paths.Roms, "nes", "B"));

        StorageFolders.RemoveEmptyLibraryFolders(paths, [Handler("gb", embed), Handler("nes", embed), Handler("snes", embed)], NullLogger.Instance);

        Assert.That(Entries(paths.Roms), Is.EqualTo(new[] { "nes" }));
        Assert.That(Entries(Path.Combine(paths.Roms, "nes")), Is.EqualTo(new[] { "A" }));
        Assert.That(File.Exists(Path.Combine(paths.Roms, "nes", "A", "Adventure.zip")), Is.True);
    }

    [Test]
    public void RemoveEmptyLibraryFolders_KeepsHiddenFiles_Subfolders_AndOtherNames()
    {
        var paths = Paths();
        var hidden = Path.Combine(paths.Roms, "gb", "A", ".hidden");
        Directory.CreateDirectory(Path.GetDirectoryName(hidden)!);
        File.WriteAllText(hidden, "x");
        if (OperatingSystem.IsWindows())
            File.SetAttributes(hidden, FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Combine(paths.Roms, "gb", "B", "nested"));
        Directory.CreateDirectory(Path.Combine(paths.Roms, "gb", "_extra"));

        StorageFolders.RemoveEmptyLibraryFolders(paths, [Handler("gb")], NullLogger.Instance);

        Assert.That(Entries(Path.Combine(paths.Roms, "gb")), Is.EqualTo(new[] { "A", "B", "_extra" }));
        Assert.That(Directory.Exists(Path.Combine(paths.Roms, "gb", "B", "nested")), Is.True, "only bucket folders themselves are candidates");
    }

    [Test]
    public void RemoveEmptyLibraryFolders_NoLibraryFolders_DoesNothingQuietly()
    {
        var logger = Substitute.For<ILogger>();

        StorageFolders.RemoveEmptyLibraryFolders(Paths(), [Handler("gb"), Handler("ext", embed: false)], logger);

        Assert.That(Warnings(logger), Is.Zero, "missing folders are skipped silently");
    }

    [TestCase(true, "gb", TestName = "RemoveEmptyLibraryFolders_LinkedFolder_IsLeftAlone embedded system folder")]
    [TestCase(true, "gb/A", TestName = "RemoveEmptyLibraryFolders_LinkedFolder_IsLeftAlone embedded bucket")]
    [TestCase(false, "gb", TestName = "RemoveEmptyLibraryFolders_LinkedFolder_IsLeftAlone external system folder")]
    [TestCase(false, "gb/A", TestName = "RemoveEmptyLibraryFolders_LinkedFolder_IsLeftAlone external bucket")]
    public void RemoveEmptyLibraryFolders_LinkedFolder_IsLeftAlone(bool embed, string linked)
    {
        var paths = Paths();
        var link = Path.Combine(paths.Roms, linked);
        var outside = LinkOutside(link);
        Directory.CreateDirectory(Path.Combine(outside, "Z"));
        if (linked.Contains('/'))
            Directory.CreateDirectory(Path.Combine(paths.Roms, "gb", "B"));
        var logger = Substitute.For<ILogger>();
        try
        {
            StorageFolders.RemoveEmptyLibraryFolders(paths, [Handler("gb", embed)], logger);

            Assert.That(Directory.Exists(Path.Combine(outside, "Z")), Is.True, "nothing is deleted through the link");
            Assert.That(Directory.Exists(link), Is.True, "the link itself stays");
            Assert.That(Warnings(logger), Is.EqualTo(1));
            if (linked.Contains('/'))
                Assert.That(Directory.Exists(Path.Combine(paths.Roms, "gb", "B")), Is.False, "its empty siblings still go");
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(link);
        }
    }

    [Test]
    public void RemoveEmptyLibraryFolders_Failure_LogsInsteadOfThrowing_AndGoesOn()
    {
        var paths = Paths();
        Directory.CreateDirectory(Path.Combine(paths.Roms, "gb", "A"));
        var logger = Substitute.For<ILogger>();

        StorageFolders.RemoveEmptyLibraryFolders(paths, [Handler("../escape"), Handler("gb")], logger);

        Assert.That(Warnings(logger), Is.EqualTo(1));
        Assert.That(Directory.Exists(Path.Combine(paths.Roms, "gb")), Is.False, "the next system is still cleaned");
    }
}
