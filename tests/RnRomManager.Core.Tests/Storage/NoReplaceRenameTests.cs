using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Storage;

/// <summary>Spec W6: the same-filesystem rename that never replaces and never copies.</summary>
public class NoReplaceRenameTests
{
    private TempDir _dir = null!;

    [SetUp]
    public void SetUp() => _dir = new TempDir();

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private string Write(string name, string content)
    {
        var path = _dir.Combine(name);
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public void SameVolume_RenamesTheFile()
    {
        var source = Write("a.nsp.tmp", "rom");
        var destination = _dir.Combine("a.nsp");

        var result = NoReplaceRename.Move(source, destination);

        Assert.That(result, Is.EqualTo(new NoReplaceRenameResult(NoReplaceRenameStatus.Renamed)));
        Assert.That(result.Renamed, Is.True);
        Assert.That((File.Exists(source), File.ReadAllText(destination)), Is.EqualTo((false, "rom")));
    }

    [Test]
    public void IntoAnotherFolderOfTheSameVolume_Renames()
    {
        var source = Write("a.nsp", "rom");
        Directory.CreateDirectory(_dir.Combine("O"));

        Assert.That(NoReplaceRename.Move(source, _dir.Combine("O", "a.nsp")).Status, Is.EqualTo(NoReplaceRenameStatus.Renamed));
        Assert.That(File.ReadAllText(_dir.Combine("O", "a.nsp")), Is.EqualTo("rom"));
    }

    [Test]
    public void OccupiedDestination_IsRefused_AndBothFilesAreKept()
    {
        var source = Write("incoming.nsp", "incoming");
        var destination = Write("a.nsp", "occupant");

        var result = NoReplaceRename.Move(source, destination);

        Assert.That(result.Status, Is.EqualTo(NoReplaceRenameStatus.DestinationExists));
        Assert.That((result.Renamed, result.NativeError != 0, result.Details), Is.EqualTo((false, true, $"{destination} already exists")));
        Assert.That((File.ReadAllText(source), File.ReadAllText(destination)), Is.EqualTo(("incoming", "occupant")));
    }

    [Test]
    public void DestinationThatIsADirectory_IsRefused()
    {
        var source = Write("incoming.nsp", "incoming");
        Directory.CreateDirectory(_dir.Combine("a.nsp"));

        Assert.That(NoReplaceRename.Move(source, _dir.Combine("a.nsp")).Renamed, Is.False);
        Assert.That((File.ReadAllText(source), Directory.Exists(_dir.Combine("a.nsp"))), Is.EqualTo(("incoming", true)));
    }

    [Test]
    public void MissingSource_OrMissingDestinationFolder_IsNotFound()
    {
        var source = Write("a.nsp", "rom");

        Assert.That(NoReplaceRename.Move(_dir.Combine("nosuch.nsp"), _dir.Combine("b.nsp")).Status, Is.EqualTo(NoReplaceRenameStatus.NotFound));
        Assert.That(NoReplaceRename.Move(source, _dir.Combine("nosuch", "b.nsp")).Status, Is.EqualTo(NoReplaceRenameStatus.NotFound));
        Assert.That(File.ReadAllText(source), Is.EqualTo("rom"));
    }

    [Test]
    public void Directory_CanBeRenamed_WithoutReplacing()
    {
        Directory.CreateDirectory(_dir.Combine("staged"));
        File.WriteAllText(_dir.Combine("staged", "x"), "x");
        Directory.CreateDirectory(_dir.Combine("occupied"));

        Assert.That(NoReplaceRename.Move(_dir.Combine("staged"), _dir.Combine("occupied")).Renamed, Is.False);
        Assert.That(NoReplaceRename.Move(_dir.Combine("staged"), _dir.Combine("live")).Renamed, Is.True);
        Assert.That(File.Exists(_dir.Combine("live", "x")), Is.True);
    }

    [Test]
    [Platform("Linux")]
    public void DifferentFilesystems_IsCrossDevice_NotACopy()
    {
        // /dev/shm is its own tmpfs on Linux hosts and in containers.
        if (!Directory.Exists("/dev/shm"))
            Assert.Ignore("no /dev/shm");
        var source = Write("a.nsp", "rom");
        var destination = Path.Combine("/dev/shm", "rnrm-test-" + Guid.NewGuid().ToString("N") + ".nsp");
        try
        {
            var result = NoReplaceRename.Move(source, destination);

            if (result.Renamed)
                Assert.Ignore("the temp folder and /dev/shm are one filesystem here");
            Assert.That(result.Status, Is.EqualTo(NoReplaceRenameStatus.CrossDevice));
            Assert.That((File.ReadAllText(source), File.Exists(destination)), Is.EqualTo(("rom", false)), "nothing was copied");
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [TestCase(17, NoReplaceRenameStatus.DestinationExists)] // EEXIST
    [TestCase(18, NoReplaceRenameStatus.CrossDevice)] // EXDEV
    [TestCase(22, NoReplaceRenameStatus.Unsupported)] // EINVAL: the filesystem rejects RENAME_NOREPLACE
    [TestCase(38, NoReplaceRenameStatus.Unsupported)] // ENOSYS
    [TestCase(95, NoReplaceRenameStatus.Unsupported)] // EOPNOTSUPP
    [TestCase(2, NoReplaceRenameStatus.NotFound)] // ENOENT
    [TestCase(13, NoReplaceRenameStatus.Failed)] // EACCES
    public void Errno_MapsToAStatus(int errno, NoReplaceRenameStatus expected)
    {
        var result = NoReplaceRename.FromErrno(errno, "/app/ingest/ext/a.nsp", "/app/roms/ext/O/a.nsp");

        Assert.That((result.Status, result.NativeError), Is.EqualTo((expected, errno)));
        if (expected == NoReplaceRenameStatus.Unsupported)
            Assert.That(result.Details, Is.EqualTo("no-replace rename is not supported for /app/roms/ext/O/a.nsp"));
    }

    [TestCase(183, NoReplaceRenameStatus.DestinationExists)] // ERROR_ALREADY_EXISTS
    [TestCase(80, NoReplaceRenameStatus.DestinationExists)] // ERROR_FILE_EXISTS
    [TestCase(17, NoReplaceRenameStatus.CrossDevice)] // ERROR_NOT_SAME_DEVICE
    [TestCase(2, NoReplaceRenameStatus.NotFound)]
    [TestCase(3, NoReplaceRenameStatus.NotFound)]
    [TestCase(32, NoReplaceRenameStatus.Failed)] // ERROR_SHARING_VIOLATION
    [TestCase(5, NoReplaceRenameStatus.Failed)] // ERROR_ACCESS_DENIED
    public void WindowsError_MapsToAStatus(int error, NoReplaceRenameStatus expected) =>
        Assert.That(NoReplaceRename.FromWindowsError(error, @"C:\ingest\a.nsp", @"D:\roms\a.nsp").Status, Is.EqualTo(expected));

    [Test]
    [Platform("Win")]
    public void Windows_SharingViolation_IsFailed_AndTheSourceStays()
    {
        var source = Write("a.nsp", "rom");
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = NoReplaceRename.Move(source, _dir.Combine("b.nsp"));

            Assert.That((result.Status, result.NativeError), Is.EqualTo((NoReplaceRenameStatus.Failed, 32)));
        }
        Assert.That((File.Exists(source), File.Exists(_dir.Combine("b.nsp"))), Is.EqualTo((true, false)));
    }

    [Test]
    [Platform("Win")]
    public void Windows_PathOver260Characters_IsRenamed()
    {
        // Three 100-character folders put every path here well past MAX_PATH; each name is an ordinary one.
        var deep = Path.Combine(_dir.Path, new string('a', 100), new string('b', 100), new string('c', 100));
        Directory.CreateDirectory(deep);
        var source = Path.Combine(deep, "Orbit.Orbit [v0].nsp.tmp");
        var destination = Path.Combine(deep, "Orbit.Orbit [v0].nsp");
        File.WriteAllText(source, "rom");
        File.WriteAllText(Path.Combine(deep, "occupied.nsp"), "occupant");

        Assert.That(destination, Has.Length.GreaterThan(300));
        Assert.That(NoReplaceRename.Move(source, Path.Combine(deep, "occupied.nsp")).Status, Is.EqualTo(NoReplaceRenameStatus.DestinationExists));
        Assert.That(NoReplaceRename.Move(source, destination).Status, Is.EqualTo(NoReplaceRenameStatus.Renamed));
        Assert.That((File.Exists(source), File.ReadAllText(destination)), Is.EqualTo((false, "rom")));
    }

    [TestCase(@"C:\app\roms\ext\O\a.nsp", @"\\?\C:\app\roms\ext\O\a.nsp")]
    [TestCase(@"d:\x", @"\\?\d:\x")]
    [TestCase(@"\\server\share\roms\a.nsp", @"\\?\UNC\server\share\roms\a.nsp")]
    [TestCase(@"\\?\C:\already\extended", @"\\?\C:\already\extended")]
    [TestCase(@"\\?\UNC\server\share\x", @"\\?\UNC\server\share\x")]
    [TestCase(@"\\.\pipe\x", @"\\.\pipe\x")]
    public void ExtendedLength_ConvertsDriveAndUncPaths(string path, string expected) =>
        Assert.That(NativePath.ToExtendedLength(path), Is.EqualTo(expected));
}
