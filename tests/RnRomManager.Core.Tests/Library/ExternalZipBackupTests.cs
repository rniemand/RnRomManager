using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Spec W3: the owned, verified backup of a source ZIP that a multi-release move takes first.</summary>
public class ExternalZipBackupTests
{
    private const string Note = "notes/Space Game (USA).nsp.md";

    private TempDir _dir = null!;
    private string _roms = null!;
    private string _folder = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _roms = Directory.CreateDirectory(_dir.Combine("roms")).FullName;
        _folder = Directory.CreateDirectory(Path.Combine(_roms, "ext", "S")).FullName;
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    /// <summary>A source ZIP with a release's metadata and its note (the backup copies bytes, so any ZIP will do).</summary>
    private (string Path, ExternalFileSnapshot Snapshot) Source(string name = "Space Game.zip")
    {
        var path = Path.Combine(_folder, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var info = new StreamWriter(zip.CreateEntry("info.json").Open()))
                info.Write("{\"roms\":[{\"fileName\":\"Space Game (USA).nsp\"}]}");
            using var note = new StreamWriter(zip.CreateEntry(Note).Open());
            note.Write("the original note");
        }
        return (path, ExternalRomPath.ClassifyFile(_roms, path).Snapshot!);
    }

    private string[] Names() => [.. Directory.GetFiles(_folder).Select(Path.GetFileName).Order()!];

    private Task RecoverAsync()
    {
        var jobs = Substitute.For<IJobRepository>();
        jobs.AbortRunningAsync(default, default).ReturnsForAnyArgs([]);
        var paths = Options.Create(new PathsOptions { Roms = _roms, Cache = _dir.Combine("cache") });
        return new CrashRecovery(jobs, paths, TimeProvider.System, NullLogger<CrashRecovery>.Instance).RunAsync(default);
    }

    [Test]
    public async Task ValidBackup_HoldsTheOriginalReleaseAndNote_AndSurvivesCrashRecovery()
    {
        var (zip, snapshot) = Source();
        File.WriteAllText(zip + ".tmp", "a ZIP writer's leftover"); // what crash recovery does remove

        var backup = await ExternalZipBackup.CreateAsync(_roms, zip, snapshot);
        await RecoverAsync();

        Assert.That(Path.GetFileName(backup.Path), Is.EqualTo("Space Game.zip" + ExternalRomName.MoveBackupSuffix));
        Assert.That(File.ReadAllBytes(backup.Path), Is.EqualTo(File.ReadAllBytes(zip)));
        using (var copy = ZipFile.OpenRead(backup.Path))
        using (var note = new StreamReader(copy.GetEntry(Note)!.Open()))
            Assert.That(note.ReadToEnd(), Is.EqualTo("the original note"));
        Assert.That(backup.Snapshot, Is.EqualTo(ExternalRomPath.ClassifyFile(_roms, backup.Path).Snapshot));
        Assert.That(Names(), Is.EqualTo(new[] { "Space Game.zip", "Space Game.zip" + ExternalRomName.MoveBackupSuffix }));
    }

    [Test]
    public async Task LongSourceName_GetsLegalUniqueBackupAndStagingNames()
    {
        var name = new string('n', 246) + ".zip"; // 250 bytes
        var (zip, snapshot) = Source(name);
        var taken = ExternalRomName.AllocateMoveBackup(name, new ExternalNameReservations([]));
        File.WriteAllText(Path.Combine(_folder, taken), "someone else's");

        var backup = await ExternalZipBackup.CreateAsync(_roms, zip, snapshot);

        var backupName = Path.GetFileName(backup.Path);
        Assert.That(backupName, Is.Not.EqualTo(taken).IgnoreCase);
        Assert.That(backupName, Does.EndWith(" (2)" + ExternalRomName.MoveBackupSuffix));
        Assert.That(Encoding.UTF8.GetByteCount(backupName + ExternalRomName.RecordSuffix + ExternalRomName.PartialSuffix), Is.LessThanOrEqualTo(ExternalRomName.MaxComponentBytes), "the longest of the four names fits");
        Assert.That(File.ReadAllText(Path.Combine(_folder, taken)), Is.EqualTo("someone else's"));
    }

    [Test]
    public async Task InterruptedBeforePromotion_LeavesOnlyAPartial_ThatCrashRecoveryKeeps()
    {
        var (zip, snapshot) = Source();
        var hooks = new ExternalZipBackupHooks { BeforePromotion = _ => throw new InvalidOperationException("the process stops here") };

        Assert.That(() => ExternalZipBackup.CreateAsync(_roms, zip, snapshot, hooks), Throws.InvalidOperationException);
        await RecoverAsync();

        Assert.That(Names(), Is.EqualTo(new[] { "Space Game.zip", "Space Game.zip" + ExternalRomName.MoveBackupSuffix + ExternalRomName.PartialSuffix }));
    }

    [TestCase("before reopen", TestName = "ReplacedStagingFile_IsNeverPromotedOrRemoved before reopen")]
    [TestCase("after verification", TestName = "ReplacedStagingFile_IsNeverPromotedOrRemoved after verification")]
    public void ReplacedStagingFile_IsNeverPromotedOrRemoved(string when)
    {
        var (zip, snapshot) = Source();
        var original = File.ReadAllBytes(zip);
        void Replace(string partial)
        {
            File.Delete(partial);
            File.WriteAllText(partial, "an occupant");
        }
        var hooks = when == "before reopen" ? new ExternalZipBackupHooks { AfterCopy = Replace } : new ExternalZipBackupHooks { BeforePromotion = Replace };

        Assert.That(() => ExternalZipBackup.CreateAsync(_roms, zip, snapshot, hooks), Throws.InstanceOf<IOException>());

        var partial = Path.Combine(_folder, "Space Game.zip" + ExternalRomName.MoveBackupSuffix + ExternalRomName.PartialSuffix);
        Assert.That(File.ReadAllText(partial), Is.EqualTo("an occupant"));
        Assert.That(File.Exists(Path.Combine(_folder, "Space Game.zip" + ExternalRomName.MoveBackupSuffix)), Is.False);
        Assert.That(File.ReadAllBytes(zip), Is.EqualTo(original), "the source is untouched");
    }

    [TestCase("occupied", TestName = "BackupNameTakenOrReplaced_TheOccupantIsKept occupied before promotion")]
    [TestCase("replaced", TestName = "BackupNameTakenOrReplaced_TheOccupantIsKept replaced after promotion")]
    public void BackupNameTakenOrReplaced_TheOccupantIsKept(string how)
    {
        var (zip, snapshot) = Source();
        var backup = Path.Combine(_folder, "Space Game.zip" + ExternalRomName.MoveBackupSuffix);
        var hooks = how == "occupied"
            ? new ExternalZipBackupHooks { BeforePromotion = _ => File.WriteAllText(backup, "an occupant") }
            : new ExternalZipBackupHooks { AfterPromotion = path => { File.Delete(path); File.WriteAllText(path, "an occupant"); } };

        Assert.That(() => ExternalZipBackup.CreateAsync(_roms, zip, snapshot, hooks), Throws.InstanceOf<IOException>());

        Assert.That(File.ReadAllText(backup), Is.EqualTo("an occupant"));
        Assert.That(Names(), Is.EqualTo(new[] { "Space Game.zip", Path.GetFileName(backup) }), "the owned staging file was cleaned up");
    }

    [Test]
    public void SourceChangedSinceItWasOpened_FailsWithoutTouchingIt()
    {
        var (zip, snapshot) = Source();
        File.AppendAllText(zip, "more");
        var changed = File.ReadAllBytes(zip);

        Assert.That(() => ExternalZipBackup.CreateAsync(_roms, zip, snapshot), Throws.InstanceOf<IOException>());

        Assert.That(File.ReadAllBytes(zip), Is.EqualTo(changed));
        Assert.That(Names(), Is.EqualTo(new[] { "Space Game.zip" }));
    }
}
