using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Tests.Storage;

/// <summary>Spec W6 within one filesystem: staged no-replace placement, the promoted snapshot, and undo.</summary>
public class ExternalRomMoveTests
{
    private string _root = null!;
    private string _source = null!;
    private string _destination = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("rnrm-move-").FullName;
        Directory.CreateDirectory(Path.Combine(_root, "ingest"));
        Directory.CreateDirectory(Path.Combine(_root, "roms"));
        _source = Path.Combine(_root, "ingest", "game.nsp");
        _destination = Path.Combine(_root, "roms", "Game.game.nsp");
        File.WriteAllText(_source, "rom bytes");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private ExternalFileSnapshot Snapshot(string path) => ExternalRomPath.ClassifyFile(_root, path).Snapshot!;

    private ExternalMoveResult Move(string source, string destination, ExternalFileSnapshot expected, ExternalMoveHooks? hooks = null,
        string? sha1 = null) =>
        ExternalRomMove.Move(_root, source, destination, expected, NullLogger.Instance, sha1, hooks);

    [Test]
    public void Moves_AndReturnsTheSnapshotOfThePromotedFile()
    {
        var expected = Snapshot(_source);

        var result = Move(_source, _destination, expected);

        Assert.That((result.Status, result.Path, result.Snapshot), Is.EqualTo((ExternalMoveStatus.Moved, _destination, expected)));
        Assert.That(Snapshot(_destination), Is.EqualTo(expected), "a rename keeps identity, length and time");
        Assert.That(Directory.GetFiles(Path.Combine(_root, "ingest")), Is.Empty);
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.EqualTo(new[] { _destination }), "no staging file is left");
    }

    [TestCase(".tmp", TestName = "OccupiedName_IsNeverOverwritten staging name")]
    [TestCase("", TestName = "OccupiedName_IsNeverOverwritten destination")]
    public void OccupiedName_IsNeverOverwritten(string suffix)
    {
        var occupant = _destination + suffix;
        File.WriteAllText(occupant, "someone else's");

        var result = Move(_source, _destination, Snapshot(_source));

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _source)));
        Assert.That(File.ReadAllText(occupant), Is.EqualTo("someone else's"));
        Assert.That(File.ReadAllText(_source), Is.EqualTo("rom bytes"), "the source is back where it was");
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.EqualTo(new[] { occupant }));
    }

    [Test]
    public void SourceReplacedSinceItWasChecked_IsRejectedWithoutMoving()
    {
        var expected = Snapshot(_source);
        File.Delete(_source);
        File.WriteAllText(_source, "another file");

        var result = Move(_source, _destination, expected);

        Assert.That(result.Status, Is.EqualTo(ExternalMoveStatus.SourceChanged));
        Assert.That(File.ReadAllText(_source), Is.EqualTo("another file"));
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.Empty);
    }

    [Test]
    public void StagedFileChanged_IsPutBack_NotPromoted()
    {
        var expected = Snapshot(_source);
        var hooks = new ExternalMoveHooks { AfterStaging = staging => File.AppendAllText(staging, " and more") };

        var result = Move(_source, _destination, expected, hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.SourceChanged, _source)));
        Assert.That(File.ReadAllText(_source), Is.EqualTo("rom bytes and more"));
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.Empty);
    }

    [Test]
    public void StagedFileGone_IsReportedAsNotFound_WithNoSurvivorClaimed()
    {
        var hooks = new ExternalMoveHooks { AfterStaging = File.Delete };

        var result = Move(_source, _destination, Snapshot(_source), hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, (string?)null)));
        Assert.That(result.Details, Does.Contain(_destination + ".tmp"));
    }

    [TestCase(18)] // EXDEV
    [TestCase(95)] // EOPNOTSUPP
    public void UnsupportedPromotion_PublishesBeforeDeletingTheOriginal(int stagingErrno)
    {
        var expected = Snapshot(_source);
        var hooks = new ExternalMoveHooks
        {
            Rename = (from, to) => NoReplaceRename.FromErrno(from == _source ? stagingErrno : 95, from, to),
            DeleteSource = source =>
            {
                Assert.That(Snapshot(source), Is.EqualTo(expected));
                Assert.That(File.ReadAllText(_destination), Is.EqualTo("rom bytes"));
                File.Delete(source);
            },
        };

        var result = Move(_source, _destination, expected, hooks, sha1: Sha1("rom bytes"));

        Assert.That((result.Status, result.Path, result.Snapshot), Is.EqualTo((ExternalMoveStatus.Moved, _destination, Snapshot(_destination))));
        Assert.That(File.Exists(_source), Is.False);
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.EqualTo(new[] { _destination }));
    }

    [Test]
    public void UnsupportedPromotion_LeavesALateOccupantAndTheOriginalInPlace()
    {
        var expected = Snapshot(_source);
        var hooks = new ExternalMoveHooks
        {
            Rename = (from, to) =>
            {
                if (from == _source)
                    return NoReplaceRename.FromErrno(18, from, to);
                File.WriteAllText(to, "another file");
                return NoReplaceRename.FromErrno(95, from, to);
            },
            DeleteSource = _ => Assert.Fail("a collision must keep the source"),
        };

        var result = Move(_source, _destination, expected, hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _source)));
        Assert.That(result.Details, Does.Contain("already exists"));
        Assert.That(Snapshot(_source), Is.EqualTo(expected));
        Assert.That(File.ReadAllText(_destination), Is.EqualTo("another file"));
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.EqualTo(new[] { _destination }));
    }

    [Test]
    public void SharingFailure_OnTheFirstRename_LeavesTheSource()
    {
        var hooks = new ExternalMoveHooks { Rename = (from, to) => NoReplaceRename.FromWindowsError(32, from, to) };

        var result = Move(_source, _destination, Snapshot(_source), hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _source)));
        Assert.That(File.ReadAllText(_source), Is.EqualTo("rom bytes"));
    }

    [Test]
    public void Move_WorksWhileADownloadHoldsTheFileOpen()
    {
        using var download = ExternalRomPath.OpenRead(_root, _source, FileShare.Read | FileShare.Delete).Stream!;

        var result = Move(_source, _destination, Snapshot(_source));

        Assert.That(result.Status, Is.EqualTo(ExternalMoveStatus.Moved));
        Assert.That(new StreamReader(download).ReadToEnd(), Is.EqualTo("rom bytes"), "the open stream keeps reading");
    }

    // --- across volumes: the first rename (source to staging) reports a different device, so the file is copied ---

    private static string Sha1(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Different device from <paramref name="crossFrom"/>; a real rename otherwise.</summary>
    private static Func<string, string, NoReplaceRenameResult> Rename(string crossFrom) =>
        (from, to) => from == crossFrom ? NoReplaceRename.FromWindowsError(17, from, to)
            : NoReplaceRename.Move(from, to);

    [Test]
    public void AcrossVolumes_CopiesVerifiesAndRemovesTheSource_EvenWithADownloadOpen()
    {
        using var download = ExternalRomPath.OpenRead(_root, _source, FileShare.Read | FileShare.Delete).Stream!;

        var expected = Snapshot(_source);
        var hooks = new ExternalMoveHooks
        {
            Rename = Rename(_source),
            DeleteSource = source =>
            {
                Assert.That(Snapshot(source), Is.EqualTo(expected));
                Assert.That(File.ReadAllText(_destination), Is.EqualTo("rom bytes"));
                File.Delete(source);
            },
        };
        var result = Move(_source, _destination, expected, hooks, sha1: Sha1("rom bytes"));

        Assert.That((result.Status, result.Path), Is.EqualTo((ExternalMoveStatus.Moved, _destination)));
        Assert.That(result.Snapshot, Is.EqualTo(Snapshot(_destination)), "the snapshot is the copy's, a new file");
        Assert.That(File.ReadAllText(_destination), Is.EqualTo("rom bytes"));
        Assert.That(File.Exists(_source), Is.False);
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.EqualTo(new[] { _destination }));
    }

    private sealed class FailingStream(Stream inner) : MemoryStream
    {
        private bool _served;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served)
                throw new IOException("disk full");
            _served = true;
            return inner.Read(buffer, offset, 3);
        }
    }

    [Test]
    public void AcrossVolumes_PartialCopy_LeavesTheSource_AndRemovesOnlyItsOwnTemp()
    {
        var bystander = Path.Combine(_root, "roms", "other.nsp.tmp");
        File.WriteAllText(bystander, "not ours");
        var hooks = new ExternalMoveHooks { Rename = Rename(_source), WrapSource = s => new FailingStream(s) };

        var result = Move(_source, _destination, Snapshot(_source), hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _source)));
        Assert.That(result.Details, Does.Contain("disk full"));
        Assert.That(File.ReadAllText(_source), Is.EqualTo("rom bytes"));
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.EqualTo(new[] { bystander }));
    }

    [Test]
    public void AcrossVolumes_OccupiedTemp_IsLeftIntact()
    {
        File.WriteAllText(_destination + ".tmp", "someone else's");

        var result = Move(_source, _destination, Snapshot(_source), new ExternalMoveHooks { Rename = Rename(_source) });

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _source)));
        Assert.That(File.ReadAllText(_destination + ".tmp"), Is.EqualTo("someone else's"));
        Assert.That(File.Exists(_source), Is.True);
    }

    [Test]
    public void AcrossVolumes_WrongSha1_KeepsTheSource_AndRemovesTheCopy()
    {
        var result = Move(_source, _destination, Snapshot(_source), new ExternalMoveHooks { Rename = Rename(_source) }, sha1: Sha1("other bytes"));

        Assert.That(result.Status, Is.EqualTo(ExternalMoveStatus.SourceChanged));
        Assert.That(File.Exists(_source), Is.True);
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.Empty);
    }

    [Test]
    public void AcrossVolumes_SourcePathReplacedAfterAMatchingCopy_IsPreserved()
    {
        var hooks = new ExternalMoveHooks
        {
            Rename = Rename(_source),
            AfterStaging = _ =>
            {
                File.Delete(_source); // the copy already read the right bytes
                File.WriteAllText(_source, "newcomer!"); // same length
            },
        };

        var result = Move(_source, _destination, Snapshot(_source), hooks, sha1: Sha1("rom bytes"));

        Assert.That(result.Status, Is.EqualTo(ExternalMoveStatus.SourceChanged));
        Assert.That(File.ReadAllText(_source), Is.EqualTo("newcomer!"));
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.Empty, "only the owned copy was removed");
    }

    [Test]
    public void AcrossVolumes_TempReplacedBeforeUnlink_LeavesTheSource_AndTheOccupant()
    {
        var hooks = new ExternalMoveHooks
        {
            Rename = Rename(_source),
            AfterStaging = staging =>
            {
                File.Move(staging, staging + ".gone"); // keep the old file alive so its identity isn't reused
                File.WriteAllText(staging, "rom bytes"); // same bytes, another file
            },
        };

        var result = Move(_source, _destination, Snapshot(_source), hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _source)));
        Assert.That(File.Exists(_source), Is.True);
        Assert.That(File.Exists(_destination + ".tmp"), Is.True, "the occupant is not ours to delete");
        Assert.That(File.Exists(_destination), Is.False);
    }

    [Test]
    public void AcrossVolumes_DestinationReplacedDuringPromotion_LeavesTheSourceAndOccupant()
    {
        var hooks = new ExternalMoveHooks
        {
            Rename = (from, to) =>
            {
                if (from == _source)
                    return NoReplaceRename.FromWindowsError(17, from, to);
                var result = NoReplaceRename.Move(from, to);
                Assert.That(result.Renamed, Is.True);
                File.Move(to, to + ".gone");
                File.WriteAllText(to, "new occupant");
                return result;
            },
        };

        var result = Move(_source, _destination, Snapshot(_source), hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _source)));
        Assert.That(File.ReadAllText(_source), Is.EqualTo("rom bytes"));
        Assert.That(File.ReadAllText(_destination), Is.EqualTo("new occupant"));
    }

    [Test]
    public void AcrossVolumes_UnlinkFails_KeepsTheSource_AndRemovesTheCopy()
    {
        var hooks = new ExternalMoveHooks { Rename = Rename(_source), DeleteSource = _ => throw new IOException("in use") };

        var result = Move(_source, _destination, Snapshot(_source), hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _source)));
        Assert.That(File.Exists(_source), Is.True);
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.Empty);
    }

    [Test]
    public void AcrossVolumes_PromotionFails_LeavesTheOriginalInPlace()
    {
        var expected = Snapshot(_source);
        var hooks = new ExternalMoveHooks
        {
            Rename = (from, to) => from == _source
                ? NoReplaceRename.FromErrno(18, from, to)
                : NoReplaceRename.FromErrno(5, from, to),
            DeleteSource = _ => Assert.Fail("publication failed before the source could be deleted"),
        };

        var result = Move(_source, _destination, expected, hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _source)));
        Assert.That(result.Details, Does.Contain("errno 5"));
        Assert.That(Snapshot(_source), Is.EqualTo(expected));
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.Empty);
    }

    [Test]
    public void AcrossVolumes_SourceReplacedDuringPublication_PreservesBothFiles()
    {
        var hooks = new ExternalMoveHooks
        {
            Rename = (from, to) =>
            {
                if (from == _source)
                    return NoReplaceRename.FromErrno(18, from, to);
                var result = NoReplaceRename.Move(from, to);
                File.Move(_source, _source + ".old");
                File.WriteAllText(_source, "new source");
                return result;
            },
        };

        var result = Move(_source, _destination, Snapshot(_source), hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _destination)));
        Assert.That(File.ReadAllText(_source), Is.EqualTo("new source"));
        Assert.That(File.ReadAllText(_destination), Is.EqualTo("rom bytes"));
    }

    [Test]
    public void PromotionFails_AndPuttingBackFails_ReportsTheStagingPathAsTheSurvivor()
    {
        var calls = 0;
        var hooks = new ExternalMoveHooks
        {
            Rename = (from, to) => ++calls == 1 ? NoReplaceRename.Move(from, to) : new NoReplaceRenameResult(NoReplaceRenameStatus.Failed, 5, "denied"),
        };

        var result = Move(_source, _destination, Snapshot(_source), hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _destination + ".tmp")));
        Assert.That(File.ReadAllText(_destination + ".tmp"), Is.EqualTo("rom bytes"));
    }

    [Test]
    public void Undo_RestoresTheExactOriginalPath()
    {
        var placed = Move(_source, _destination, Snapshot(_source));

        var undone = Move(placed.Path!, _source, placed.Snapshot!);

        Assert.That((undone.Status, undone.Path), Is.EqualTo((ExternalMoveStatus.Moved, _source)));
        Assert.That(File.ReadAllText(_source), Is.EqualTo("rom bytes"));
        Assert.That(Directory.GetFiles(Path.Combine(_root, "roms")), Is.Empty);
    }

    [Test]
    public void Undo_RefusesAReplacementAtThePromotedPath()
    {
        var placed = Move(_source, _destination, Snapshot(_source));
        File.Delete(_destination);
        File.WriteAllText(_destination, "a newer occupant");

        var undone = Move(placed.Path!, _source, placed.Snapshot!);

        Assert.That(undone.Status, Is.EqualTo(ExternalMoveStatus.SourceChanged));
        Assert.That(File.ReadAllText(_destination), Is.EqualTo("a newer occupant"));
        Assert.That(File.Exists(_source), Is.False);
    }

    [Test]
    public void Undo_ToA255BytePointer_UsesACutStagingName_AndRestoresTheExactName()
    {
        var pointer = new string('p', 251) + ".nsp";
        var original = Path.Combine(_root, "roms", pointer);
        Assert.That(ExternalRomName.StagingName(pointer), Is.EqualTo(new string('p', 251) + ".tmp"));
        Assert.That(ExternalRomName.StagingName("a.nsp"), Is.EqualTo("a.nsp.tmp"));
        try
        {
            File.Move(_source, original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Ignore($"this filesystem can't hold a 255-byte name here: {ex.Message}");
        }
        var away = Move(original, _destination, Snapshot(original));

        var undone = Move(away.Path!, original, away.Snapshot!);

        Assert.That((undone.Status, undone.Path), Is.EqualTo((ExternalMoveStatus.Moved, original)));
    }

    // --- rollback names for a loose ingest source ---

    [Test]
    public void RollbackPath_IsTheOriginal_WhenFree_ElseAClashSibling()
    {
        File.Delete(_source);
        Assert.That(ExternalRomName.AllocateRollbackPath(_source, fitSourcePathColumn: true), Is.EqualTo(_source));

        File.WriteAllText(Path.Combine(_root, "ingest", "GAME.NSP"), "taken, in another case");
        File.WriteAllText(Path.Combine(_root, "ingest", "game (2).nsp.tmp"), "a staging name counts too");
        Assert.That(ExternalRomName.AllocateRollbackPath(_source, fitSourcePathColumn: true),
            Is.EqualTo(Path.Combine(_root, "ingest", "game (3).nsp")));
    }

    [Test]
    public void RollbackPath_Of255ByteName_IsCutToTheBudget_KeepingTheExtension()
    {
        var folder = Path.Combine(_root, "missing"); // nothing on disk: pure naming
        var original = Path.Combine(folder, new string('é', 125) + "x.nsp"); // 255 UTF-8 bytes

        var path = ExternalRomName.AllocateRollbackPath(original, fitSourcePathColumn: false)!;

        var name = Path.GetFileName(path);
        Assert.That(name, Is.EqualTo(new string('é', 123) + ".nsp"), "cut at a character boundary");
        Assert.That(Encoding.UTF8.GetByteCount(name), Is.LessThanOrEqualTo(ExternalRomName.MaxNameBytes));
    }

    [Test]
    public void RollbackPath_AtTheColumnLimit_WithItsNameTaken_GetsAShorterClashName()
    {
        // Real folders (each name within the component limit) so the original can be occupied.
        var folder = _root;
        while (folder.Length < 900)
            folder = Path.Combine(folder, new string('d', Math.Min(200, 900 - folder.Length)));
        var original = Path.Combine(folder, new string('n', 1024 - folder.Length - 1 - 4) + ".nsp"); // exactly 1,024 scalars
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(original, "a replacement took the name");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Ignore($"this filesystem can't hold a 1,024-character path: {ex.Message}");
        }

        var path = ExternalRomName.AllocateRollbackPath(original, fitSourcePathColumn: true)!;

        Assert.That(Path.GetFileName(path), Does.EndWith(" (2).nsp"));
        Assert.That(Path.GetDirectoryName(path), Is.EqualTo(folder));
        Assert.That(path.EnumerateRunes().Count(), Is.LessThanOrEqualTo(ExternalRomName.MaxSourcePathScalars));
    }

    [Test]
    public void RollbackPath_FitsThe1024ScalarSourcePath_OrIsNullWhenNothingFits()
    {
        var folder = Path.Combine(_root, new string('d', 1000 - _root.Length - 1)); // 1,000 scalars
        var original = Path.Combine(folder, new string('n', 40) + ".nsp"); // 1,045 scalars

        var path = ExternalRomName.AllocateRollbackPath(original, fitSourcePathColumn: true)!;

        Assert.That(path, Is.EqualTo(Path.Combine(folder, new string('n', 19) + ".nsp")));
        Assert.That(path.EnumerateRunes().Count(), Is.EqualTo(ExternalRomName.MaxSourcePathScalars));
        Assert.That(ExternalRomName.AllocateRollbackPath(original, fitSourcePathColumn: false), Is.EqualTo(original), "no column, no cut");

        var tooDeep = Path.Combine(_root, new string('d', 1020 - _root.Length - 1), "name.nsp");
        Assert.That(ExternalRomName.AllocateRollbackPath(tooDeep, fitSourcePathColumn: true), Is.Null, "the caller keeps the copy where it is");
    }
}
