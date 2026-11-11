using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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

    private static ExternalMoveResult Move(string source, string destination, ExternalFileSnapshot expected, ExternalMoveHooks? hooks = null,
        ILogger? logger = null) => ExternalRomMove.Move(source, destination, expected, logger ?? NullLogger.Instance, hooks);

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

    [Test]
    public void UnsupportedRename_IsFailedWithTheRequiredDetails_LoggedAsError_SourceKept()
    {
        var logger = Substitute.For<ILogger>();
        var hooks = new ExternalMoveHooks { Rename = (_, to) => new NoReplaceRenameResult(NoReplaceRenameStatus.Unsupported, 22, "x " + to) };

        var result = Move(_source, _destination, Snapshot(_source), hooks, logger);

        Assert.That((result.Status, result.Details, result.SurvivingPath),
            Is.EqualTo((ExternalMoveStatus.Failed, $"no-replace rename is not supported for {_destination}", _source)));
        Assert.That(File.Exists(_source), Is.True);
        logger.Received(1).Log(LogLevel.Error, Arg.Any<EventId>(), Arg.Any<object>(), null, Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public void CrossVolumeResult_IsFailed_SourceKept()
    {
        var hooks = new ExternalMoveHooks { Rename = (from, to) => NoReplaceRename.FromWindowsError(17, from, to) };

        var result = Move(_source, _destination, Snapshot(_source), hooks);

        Assert.That((result.Status, result.SurvivingPath), Is.EqualTo((ExternalMoveStatus.Failed, _source)));
        Assert.That(File.Exists(_source), Is.True);
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
