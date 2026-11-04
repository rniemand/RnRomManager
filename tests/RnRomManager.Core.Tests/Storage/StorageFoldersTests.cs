using System.Runtime.Versioning;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Storage;

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
}
