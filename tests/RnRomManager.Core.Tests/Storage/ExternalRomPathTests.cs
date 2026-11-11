using System.Diagnostics;
using System.Runtime.InteropServices;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Storage;

/// <summary>Spec G4 path classification: links, entry kinds, case variants, and absent versus unusable paths.</summary>
public class ExternalRomPathTests
{
    private TempDir _dir = null!;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _root = Path.Combine(_dir.Path, "roms");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private string Write(string relative, string content = "rom bytes")
    {
        var path = Path.Combine([_root, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string In(string relative) => Path.Combine([_root, .. relative.Split('/')]);

    /// <summary>Windows needs Developer Mode or elevation to create links; without it the test can't run.</summary>
    private static void Link(string link, string target, bool directory)
    {
        try
        {
            if (directory)
                Directory.CreateSymbolicLink(link, target);
            else
                File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Ignore($"This OS refused to create a symbolic link: {ex.Message}");
        }
    }

    // --- regular files and identity ---

    [Test]
    public void RegularFile_IsFound_WithItsLengthMtimeAndIdentity()
    {
        var path = Write("ext/O/Orbit.Orbit [v0].nsp", "12345");
        var mtime = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, mtime);

        var check = ExternalRomPath.ClassifyFile(_root, path);

        Assert.That((check.Status, check.Path, check.Details), Is.EqualTo((ExternalPathStatus.Found, path, (string?)null)));
        Assert.That((check.Snapshot!.Length, check.Snapshot.LastWriteTimeUtc), Is.EqualTo((5L, mtime)));
        Assert.That(check.Snapshot.Identity, Is.Not.EqualTo(default(FileIdentity)));
        Assert.That(ExternalRomPath.ClassifyFile(_root, path).Snapshot, Is.EqualTo(check.Snapshot), "stable while the file is untouched");
    }

    [Test]
    public void Identity_FollowsTheFileThroughARename_AndDiffersForAReplacement()
    {
        var path = Write("ext/a.nsp", "same");
        var original = ExternalRomPath.ClassifyFile(_root, path).Snapshot!;

        var moved = In("ext/b.nsp");
        File.Move(path, moved);
        Write("ext/a.nsp", "same"); // a different file with the same name, length and content
        File.SetLastWriteTimeUtc(path, original.LastWriteTimeUtc);

        Assert.That(ExternalRomPath.ClassifyFile(_root, moved).Snapshot!.Identity, Is.EqualTo(original.Identity));
        var replacement = ExternalRomPath.ClassifyFile(_root, path).Snapshot!;
        Assert.That((replacement.Length, replacement.LastWriteTimeUtc), Is.EqualTo((original.Length, original.LastWriteTimeUtc)));
        Assert.That(replacement.Identity, Is.Not.EqualTo(original.Identity));
    }

    // --- links and other entry kinds ---

    [Test]
    public void LeafLink_IsUnsafe_AndItsTargetIsNotInspected()
    {
        var outside = Path.Combine(_dir.Path, "outside.nsp");
        File.WriteAllText(outside, "secret");
        Directory.CreateDirectory(In("ext"));
        Link(In("ext/a.nsp"), outside, directory: false);

        var check = ExternalRomPath.ClassifyFile(_root, In("ext/a.nsp"));

        Assert.That((check.Status, check.Snapshot), Is.EqualTo((ExternalPathStatus.Unsafe, (ExternalFileSnapshot?)null)));
        Assert.That(check.Details, Does.Contain("link"));
    }

    [Test]
    public void DanglingLeafLink_IsUnsafe_NotMissing()
    {
        Directory.CreateDirectory(In("ext"));
        Link(In("ext/a.nsp"), Path.Combine(_dir.Path, "nowhere.nsp"), directory: false);

        Assert.That(ExternalRomPath.ClassifyFile(_root, In("ext/a.nsp")).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
    }

    [Test]
    public void AncestorLink_IsUnsafe_ForAFileAndForADestinationDirectory()
    {
        var outside = Path.Combine(_dir.Path, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "O"));
        File.WriteAllText(Path.Combine(outside, "O", "a.nsp"), "x");
        Link(In("ext"), outside, directory: true);

        Assert.That(ExternalRomPath.ClassifyFile(_root, In("ext/O/a.nsp")).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
        Assert.That(ExternalRomPath.ClassifyDirectory(_root, In("ext/O")).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
        Assert.That(ExternalRomPath.ClassifyDirectory(_root, In("ext/New/Deeper")).Status, Is.EqualTo(ExternalPathStatus.Unsafe), "must not be created through the link");
        Assert.That(ExternalRomPath.ClassifyDirectory(_root, In("ext")).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
    }

    [Test]
    public void Directory_WhereAFileIsExpected_IsUnsafe()
    {
        Directory.CreateDirectory(In("ext/a.nsp"));

        var check = ExternalRomPath.ClassifyFile(_root, In("ext/a.nsp"));

        Assert.That(check.Status, Is.EqualTo(ExternalPathStatus.Unsafe));
        Assert.That(check.Details, Does.Contain("not a regular file"));
    }

    [Test]
    public void File_WhereADirectoryIsExpected_IsUnsafe()
    {
        Write("ext/O");

        Assert.That(ExternalRomPath.ClassifyFile(_root, In("ext/O/a.nsp")).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
        Assert.That(ExternalRomPath.ClassifyDirectory(_root, In("ext/O")).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
        Assert.That(ExternalRomPath.ClassifyDirectory(_root, In("ext/O/New")).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
    }

    [Test]
    [Platform("Linux")]
    public void Fifo_IsUnsafe_WithoutBeingOpened()
    {
        Directory.CreateDirectory(In("ext"));
        var fifo = In("ext/a.nsp");
        Assert.That(mkfifo(fifo, 0x1B6), Is.Zero, "mkfifo");

        // Opening a FIFO for reading blocks until a writer appears; a metadata check returns at once.
        var watch = Stopwatch.StartNew();
        var check = ExternalRomPath.ClassifyFile(_root, fifo);

        Assert.That(check.Status, Is.EqualTo(ExternalPathStatus.Unsafe));
        Assert.That(check.Details, Does.Contain("not a regular file"));
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [Test]
    [Platform("Linux")]
    public void Backslash_InAComponent_IsUnsafe_NeverASeparator()
    {
        Write("ext/a.nsp");
        File.WriteAllText(Path.Combine(_root, "ext\\a.nsp"), "one component on Linux");

        var check = ExternalRomPath.ClassifyFile(_root, Path.Combine(_root, "ext\\a.nsp"));

        Assert.That(check.Status, Is.EqualTo(ExternalPathStatus.Unsafe));
        Assert.That(check.Details, Does.Contain("backslash"));
    }

    // --- case ---

    [Test]
    public void OneCaseVariant_IsFound_WithItsSpellingOnDisk()
    {
        var actual = Write("ext/O/Orbit.NSP");

        var file = ExternalRomPath.ClassifyFile(_root, In("EXT/o/orbit.nsp"));
        var directory = ExternalRomPath.ClassifyDirectory(_root, In("EXT/o"));

        Assert.That((file.Status, file.Path), Is.EqualTo((ExternalPathStatus.Found, actual)));
        Assert.That((directory.Status, directory.Path), Is.EqualTo((ExternalPathStatus.Found, In("ext/O"))));
    }

    [Test]
    public void TwoCaseVariants_AreUnsafe_EvenForAnExactMatch()
    {
        Write("ext/a.nsp", "lower");
        Write("ext/A.nsp", "upper");
        if (Directory.GetFiles(In("ext")).Length != 2)
            Assert.Ignore("This volume is case-insensitive: it can't hold both names.");

        foreach (var name in new[] { "a.nsp", "A.nsp", "a.NSP" })
        {
            var check = ExternalRomPath.ClassifyFile(_root, In("ext/" + name));

            Assert.That(check.Status, Is.EqualTo(ExternalPathStatus.Unsafe), name);
            Assert.That(check.Details, Does.Contain("A.nsp, a.nsp"));
        }
        Assert.That(File.ReadAllText(In("ext/a.nsp")) + File.ReadAllText(In("ext/A.nsp")), Is.EqualTo("lowerupper"), "both kept");
    }

    [Test]
    public void TwoCaseVariants_OfAnAncestor_AreUnsafe()
    {
        Write("ext/o/a.nsp");
        Directory.CreateDirectory(In("ext/O"));
        if (Directory.GetDirectories(In("ext")).Length != 2)
            Assert.Ignore("This volume is case-insensitive: it can't hold both names.");

        Assert.That(ExternalRomPath.ClassifyFile(_root, In("ext/o/a.nsp")).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
        Assert.That(ExternalRomPath.ClassifyDirectory(_root, In("ext/o")).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
    }

    // --- absent versus unusable ---

    [Test]
    public void AbsentLeaf_OrAbsentAncestor_IsMissing()
    {
        Write("ext/O/other.nsp");

        var leaf = ExternalRomPath.ClassifyFile(_root, In("ext/O/a.nsp"));
        var ancestor = ExternalRomPath.ClassifyFile(_root, In("ext/Q/a.nsp"));
        var system = ExternalRomPath.ClassifyFile(_root, In("nosuch/Q/a.nsp"));

        Assert.That((leaf.Status, leaf.Path, leaf.Snapshot), Is.EqualTo((ExternalPathStatus.Missing, In("ext/O/a.nsp"), (ExternalFileSnapshot?)null)));
        Assert.That((ancestor.Status, ancestor.Path), Is.EqualTo((ExternalPathStatus.Missing, In("ext/Q/a.nsp"))));
        Assert.That(system.Status, Is.EqualTo(ExternalPathStatus.Missing));
    }

    [Test]
    public void MissingRoot_IsMissing()
    {
        var root = Path.Combine(_dir.Path, "not-created");

        Assert.That(ExternalRomPath.ClassifyFile(root, Path.Combine(root, "ext", "a.nsp")).Status, Is.EqualTo(ExternalPathStatus.Missing));
        Assert.That(ExternalRomPath.ClassifyDirectory(root, Path.Combine(root, "ext")).Status, Is.EqualTo(ExternalPathStatus.Missing));
    }

    [Test]
    public void PathOutsideTheRoot_IsUnsafe()
    {
        var sibling = Path.Combine(_dir.Path, "roms2", "a.nsp");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllText(sibling, "x");
        File.WriteAllText(Path.Combine(_dir.Path, "up.nsp"), "x");

        Assert.That(ExternalRomPath.ClassifyFile(_root, sibling).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
        Assert.That(ExternalRomPath.ClassifyFile(_root, Path.Combine(_root, "..", "up.nsp")).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
        Assert.That(ExternalRomPath.ClassifyFile(_root, _root).Status, Is.EqualTo(ExternalPathStatus.Unsafe));
    }

    [Test]
    [Platform("Linux")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void UnlistableDirectory_IsUnreadable_NotMissing()
    {
        if (ProcessIdentity.IsRoot())
            Assert.Ignore("root ignores directory permissions");
        Write("ext/O/a.nsp");
        File.SetUnixFileMode(In("ext/O"), UnixFileMode.None);
        try
        {
            Assert.That(ExternalRomPath.ClassifyFile(_root, In("ext/O/a.nsp")).Status, Is.EqualTo(ExternalPathStatus.Unreadable));
            Assert.That(ExternalRomPath.ClassifyDirectory(_root, In("ext/O/New")).Status, Is.EqualTo(ExternalPathStatus.Unreadable));
        }
        finally
        {
            File.SetUnixFileMode(In("ext/O"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // --- destination directories ---

    [Test]
    public void ExistingDirectory_AndTheRootItself_AreFound()
    {
        Directory.CreateDirectory(In("ext/O"));

        Assert.That(ExternalRomPath.ClassifyDirectory(_root, In("ext/O")), Is.EqualTo(new ExternalPathCheck(ExternalPathStatus.Found, In("ext/O"))));
        Assert.That(ExternalRomPath.ClassifyDirectory(_root, _root + Path.DirectorySeparatorChar).Status, Is.EqualTo(ExternalPathStatus.Found));
    }

    [Test]
    public void MissingDirectory_UnderRealAncestors_CanBeCreated_AtTheReturnedPath()
    {
        Directory.CreateDirectory(In("ext"));

        var check = ExternalRomPath.ClassifyDirectory(_root, In("EXT/_removed/deeper"));

        // The existing part keeps its spelling on disk, so creating the result can't make a second "EXT".
        Assert.That((check.Status, check.Path), Is.EqualTo((ExternalPathStatus.Missing, In("ext/_removed/deeper"))));
        Directory.CreateDirectory(check.Path);
        Assert.That(ExternalRomPath.ClassifyDirectory(_root, In("ext/_removed/deeper")).Status, Is.EqualTo(ExternalPathStatus.Found));
        Assert.That(Directory.GetDirectories(_root), Has.Length.EqualTo(1));
    }
}
