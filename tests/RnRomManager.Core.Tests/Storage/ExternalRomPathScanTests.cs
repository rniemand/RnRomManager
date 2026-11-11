using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Storage;

/// <summary>Spec G4 checked traversal: what a folder scan lists, and what it reports instead of entering or opening.</summary>
public class ExternalRomPathScanTests
{
    private TempDir _dir = null!;
    private string _root = null!;
    private string _outside = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _root = Path.Combine(_dir.Path, "roms");
        Directory.CreateDirectory(_root);
        _outside = Path.Combine(_dir.Path, "outside");
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "Secret.zip"), "outside");
    }

    [TearDown]
    public void TearDown()
    {
        // A junction must go before the recursive delete, which would otherwise empty its target.
        foreach (var link in new[] { In("gb/L"), In("gb") })
        {
            if (Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null)
                TestLinks.RemoveDirectoryLink(link);
        }
        _dir.Dispose();
    }

    private string In(string relative) => Path.Combine([_root, .. relative.Split('/')]);

    private void Write(string relative, string content = "zip")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(In(relative))!);
        File.WriteAllText(In(relative), content);
    }

    [Test]
    public void ListsEveryRegularFileAtAnyDepth_WithSnapshotsAndSlashPaths()
    {
        Write("gb/T/Tetris.zip", "12345");
        Write("gb/T/deeper/still/Deep.zip");
        Write("gb/readme.txt");
        Write("gbc/Other.zip");

        var scan = ExternalRomPath.ScanFolder(_root, "gb");

        Assert.That(scan.Problems, Is.Empty);
        Assert.That(scan.Files.Select(f => f.RelativePath), Is.EquivalentTo(new[] { "gb/T/Tetris.zip", "gb/T/deeper/still/Deep.zip", "gb/readme.txt" }));
        var tetris = scan.Files.Single(f => f.RelativePath == "gb/T/Tetris.zip");
        Assert.That((tetris.FullPath, tetris.Snapshot), Is.EqualTo((In("gb/T/Tetris.zip"), ExternalRomPath.ClassifyFile(_root, In("gb/T/Tetris.zip")).Snapshot)));
    }

    [Test]
    public void MissingFolder_IsAnEmptyScan()
    {
        var scan = ExternalRomPath.ScanFolder(_root, "nosuch");

        Assert.That((scan.Files, scan.Problems), Is.EqualTo((Array.Empty<CheckedFile>(), Array.Empty<CheckedScanProblem>())));
    }

    [Test]
    public void FolderNameIsMatchedIgnoringCase_AndReportedAsSpelledOnDisk()
    {
        Write("GB/T/Tetris.zip");

        Assert.That(ExternalRomPath.ScanFolder(_root, "gb").Files.Single().RelativePath, Is.EqualTo("GB/T/Tetris.zip"));
    }

    [Test]
    public void LinkedSubfolder_IsReported_AndNeverEntered()
    {
        Write("gb/T/Tetris.zip");
        if (!TestLinks.TryLinkDirectory(In("gb/L"), _outside))
            Assert.Ignore("can't create a directory link here");

        var scan = ExternalRomPath.ScanFolder(_root, "gb");

        Assert.That(scan.Files.Select(f => f.RelativePath), Is.EqualTo(new[] { "gb/T/Tetris.zip" }));
        var problem = scan.Problems.Single();
        Assert.That((problem.RelativePath, problem.Status, problem.IsSpecialFile), Is.EqualTo(("gb/L", ExternalPathStatus.Unsafe, false)));
    }

    [Test]
    public void LinkedSystemFolder_IsReported_AndNeverEntered()
    {
        if (!TestLinks.TryLinkDirectory(In("gb"), _outside))
            Assert.Ignore("can't create a directory link here");

        var scan = ExternalRomPath.ScanFolder(_root, "gb");

        Assert.That(scan.Files, Is.Empty);
        Assert.That(scan.Problems.Select(p => (p.RelativePath, p.Status)), Is.EqualTo(new[] { ("gb", ExternalPathStatus.Unsafe) }));
    }

    [Test]
    public void LinkedFile_IsReported_NotListed()
    {
        Write("gb/T/Tetris.zip");
        if (!TestLinks.TryLinkFile(In("gb/T/Linked.zip"), Path.Combine(_outside, "Secret.zip")))
            Assert.Ignore("can't create a file link here");

        var scan = ExternalRomPath.ScanFolder(_root, "gb");

        Assert.That(scan.Files.Select(f => f.RelativePath), Is.EqualTo(new[] { "gb/T/Tetris.zip" }));
        Assert.That(scan.Problems.Select(p => (p.RelativePath, p.Status)), Is.EqualTo(new[] { ("gb/T/Linked.zip", ExternalPathStatus.Unsafe) }));
    }

    [Test]
    [Platform("Linux")]
    public void Fifo_IsReportedAsASpecialFile_NotListed()
    {
        Write("gb/T/Tetris.zip");
        Assert.That(TestLinks.TryCreateFifo(In("gb/T/Pipe.zip")), Is.True);

        var scan = ExternalRomPath.ScanFolder(_root, "gb");

        Assert.That(scan.Files.Select(f => f.RelativePath), Is.EqualTo(new[] { "gb/T/Tetris.zip" }));
        Assert.That(scan.Problems.Select(p => (p.RelativePath, p.Status, p.IsSpecialFile)), Is.EqualTo(new[] { ("gb/T/Pipe.zip", ExternalPathStatus.Unsafe, true) }));
    }

    [Test]
    public void CaseVariants_AreReportedOnce_AndBothSkipped()
    {
        Write("gb/T/Tetris.zip");
        Write("gb/T/tetris.zip");
        Write("gb/T/Other.zip");
        Write("gb/o/a.zip");
        Write("gb/O/b.zip");
        if (Directory.GetFiles(In("gb/T")).Length != 3)
            Assert.Ignore("This volume is case-insensitive: it can't hold both names.");

        var scan = ExternalRomPath.ScanFolder(_root, "gb");

        Assert.That(scan.Files.Select(f => f.RelativePath), Is.EqualTo(new[] { "gb/T/Other.zip" }), "neither variant, nothing below either folder");
        Assert.That(scan.Problems.Select(p => (p.RelativePath, p.Status)), Is.EqualTo(new[]
        {
            ("gb/O", ExternalPathStatus.Unsafe), ("gb/T/Tetris.zip", ExternalPathStatus.Unsafe),
        }));
        Assert.That(scan.Problems[1].Details, Does.Contain("Tetris.zip, tetris.zip"));
    }

    [Test]
    [Platform("Linux")]
    public void Backslash_InAName_IsReported_NeverSplitIntoAPath()
    {
        Write("gb/T/sub/Tetris.zip");
        File.WriteAllText(Path.Combine(In("gb/T"), "sub\\Other.zip"), "one name on Linux");

        var scan = ExternalRomPath.ScanFolder(_root, "gb");

        Assert.That(scan.Files.Select(f => f.RelativePath), Is.EqualTo(new[] { "gb/T/sub/Tetris.zip" }));
        Assert.That(scan.Problems.Select(p => (p.RelativePath, p.Status)), Is.EqualTo(new[] { ("gb/T/sub\\Other.zip", ExternalPathStatus.Unsafe) }));
    }

    [Test]
    [Platform("Linux")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void UnlistableSubfolder_IsUnreadable_AndTheRestIsStillScanned()
    {
        if (ProcessIdentity.IsRoot())
            Assert.Ignore("root ignores directory permissions");
        Write("gb/L/Locked.zip");
        Write("gb/T/Tetris.zip");
        File.SetUnixFileMode(In("gb/L"), UnixFileMode.None);
        try
        {
            var scan = ExternalRomPath.ScanFolder(_root, "gb");

            Assert.That(scan.Files.Select(f => f.RelativePath), Is.EqualTo(new[] { "gb/T/Tetris.zip" }));
            Assert.That(scan.Problems.Select(p => (p.RelativePath, p.Status)), Is.EqualTo(new[] { ("gb/L", ExternalPathStatus.Unreadable) }));
        }
        finally
        {
            File.SetUnixFileMode(In("gb/L"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // --- opening a scanned file ---

    [Test]
    public void OpenScanned_OpensTheScannedFile_AndReportsAChangeSinceTheScan()
    {
        Write("gb/T/Tetris.zip", "12345");
        var file = ExternalRomPath.ScanFolder(_root, "gb").Files.Single();

        var opened = ExternalRomPath.OpenScanned(_root, file.RelativePath, FileShare.Read | FileShare.Delete, file.Snapshot);
        using (opened.Stream)
        {
            Assert.That((opened.Status, opened.Path, opened.Snapshot), Is.EqualTo((ExternalPathStatus.Found, file.FullPath, file.Snapshot)));
            Assert.That(new StreamReader(opened.Stream!).ReadToEnd(), Is.EqualTo("12345"));
        }

        File.AppendAllText(file.FullPath, " more");
        Assert.That(ExternalRomPath.OpenScanned(_root, file.RelativePath, expected: file.Snapshot).Status, Is.EqualTo(ExternalPathStatus.Changed));
        File.Delete(file.FullPath);
        Assert.That(ExternalRomPath.OpenScanned(_root, file.RelativePath, expected: file.Snapshot).Status, Is.EqualTo(ExternalPathStatus.Missing));
    }

    [TestCase("../outside/Secret.zip")]
    [TestCase("gb//Tetris.zip")]
    [TestCase("gb/./Tetris.zip")]
    [TestCase("")]
    public void OpenScanned_PathThatIsNotAScannedRelativePath_IsUnsafe(string relative) =>
        Assert.That(ExternalRomPath.OpenScanned(_root, relative).Status, Is.EqualTo(ExternalPathStatus.Unsafe));

    [Test]
    public void OpenScanned_FolderSwappedForALinkSinceTheScan_IsUnsafe()
    {
        Write("gb/L/Secret.zip", "inside");
        var file = ExternalRomPath.ScanFolder(_root, "gb").Files.Single();
        Directory.Move(In("gb/L"), Path.Combine(_dir.Path, "moved"));
        if (!TestLinks.TryLinkDirectory(In("gb/L"), _outside))
            Assert.Ignore("can't create a directory link here");

        var opened = ExternalRomPath.OpenScanned(_root, file.RelativePath);

        Assert.That((opened.Status, opened.Stream), Is.EqualTo((ExternalPathStatus.Unsafe, (FileStream?)null)));
    }
}
