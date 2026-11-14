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
    public void EnsureSystemFolders_CreatesLetterAndIngestFoldersPerSystem()
    {
        var paths = Paths();

        StorageFolders.EnsureSystemFolders(paths, [Handler("gb"), Handler("gbc")], NullLogger.Instance);

        foreach (var id in new[] { "gb", "gbc" })
        {
            Assert.That(Directory.GetDirectories(Path.Combine(paths.Roms, id)).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "#" }.Concat(Enumerable.Range('A', 26).Select(c => ((char)c).ToString()))));
            Assert.That(Directory.GetDirectories(Path.Combine(paths.Ingest, id)).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "_duplicates", "_failed", "_removed" }));
        }
    }

    [Test]
    public void EnsureSystemFolders_IsIdempotent_AndKeepsExistingFiles()
    {
        var paths = Paths();
        StorageFolders.EnsureSystemFolders(paths, [Handler("gb")], NullLogger.Instance);
        var keep = Path.Combine(paths.Roms, "gb", "T", "Tetris.zip");
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
        Directory.CreateDirectory(Path.Combine(paths.Roms, "gb"));
        File.WriteAllText(Path.Combine(paths.Roms, "gb", "A"), "file where a folder should be");

        var ex = Assert.Throws<StorageNotWritableException>(() => StorageFolders.EnsureSystemFolders(paths, [Handler("gb")], NullLogger.Instance));

        Assert.That(ex!.Message, Does.Contain(Path.Combine(paths.Roms, "gb", "A")));
    }

    private static readonly string[] AllBuckets = ["#", .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString())];

    [Test]
    public void EnsureSystemFolders_External_CreatesBucketsAndSideFolders()
    {
        var paths = Paths();

        StorageFolders.EnsureSystemFolders(paths, [Handler("ext", embed: false)], NullLogger.Instance);

        Assert.That(Directory.GetDirectories(Path.Combine(paths.Roms, "ext")).Select(Path.GetFileName), Is.EquivalentTo(AllBuckets));
        Assert.That(Directory.GetDirectories(Path.Combine(paths.Ingest, "ext")).Select(Path.GetFileName),
            Is.EquivalentTo(new[] { "_duplicates", "_failed", "_removed" }));
    }

    /// <summary>Links <paramref name="link"/> (its parent is created) to a new empty folder outside both roots.</summary>
    private string LinkOutside(string link)
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside", Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TestLinks.TryLinkDirectory(link, outside))
            Assert.Ignore("this account can't create a directory link");
        return outside;
    }

    [TestCase("roms", "ext", TestName = "EnsureSystemFolders_LinkedExternalFolder_IsSkipped rom system folder")]
    [TestCase("roms", "ext/A", TestName = "EnsureSystemFolders_LinkedExternalFolder_IsSkipped bucket")]
    [TestCase("ingest", "ext", TestName = "EnsureSystemFolders_LinkedExternalFolder_IsSkipped ingest system folder")]
    [TestCase("ingest", "ext/_failed", TestName = "EnsureSystemFolders_LinkedExternalFolder_IsSkipped side folder")]
    public void EnsureSystemFolders_LinkedExternalFolder_IsSkipped(string tree, string linked)
    {
        var paths = Paths();
        var root = tree == "roms" ? paths.Roms : paths.Ingest;
        var link = Path.Combine(root, linked);
        var outside = LinkOutside(link);
        var logger = Substitute.For<ILogger>();
        try
        {
            StorageFolders.EnsureSystemFolders(paths, [Handler("ext", embed: false)], logger);

            Assert.That(Directory.GetFileSystemEntries(outside), Is.Empty, "nothing is created through the link");
            Assert.That(logger.ReceivedCalls().Count(c => c.GetArguments()[0] is LogLevel.Warning), Is.EqualTo(1));
            // The other tree, and the link's siblings, are still created.
            var other = tree == "roms" ? Path.Combine(paths.Ingest, "ext", "_removed") : Path.Combine(paths.Roms, "ext", "B");
            Assert.That(Directory.Exists(other), Is.True);
            if (linked.Contains('/'))
                Assert.That(Directory.Exists(Path.Combine(root, "ext", tree == "roms" ? "B" : "_removed")), Is.True);
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(link);
        }
    }
}
