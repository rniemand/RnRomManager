using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

public class RemovedFileRestorerTests
{
    private static readonly AuditActor Actor = new(42, "boss", null);
    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private JobLock _lock = null!;
    private IAuditLog _audit = null!;
    private RemovedFileRestorer _restorer = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Ingest = _dir.Combine("ingest") };
        var gb = Substitute.For<ISystemHandler>();
        gb.EmbedRomsInZip.Returns(true);
        gb.Id.Returns("gb");
        gb.Family.Returns("gameboy");
        foreach (var system in new[] { "gb", "ext" })
            Directory.CreateDirectory(Path.Combine(_paths.Ingest, system, "_removed"));
        _lock = new JobLock();
        _audit = Substitute.For<IAuditLog>();
        var options = Options.Create(_paths);
        _restorer = new RemovedFileRestorer(_lock, new SystemRegistry([gb, new ExternalTestHandler()]), options, _audit, new AuditPaths(options),
            NullLogger<RemovedFileRestorer>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private string Write(string text, params string[] relative)
    {
        var path = Path.Combine([_paths.Ingest, .. relative]);
        File.WriteAllText(path, text);
        return path;
    }

    private static IngestDeleteRef Removed(string system, string name) => new(system, IngestFileDeleter.Removed, name);

    private List<AuditEvent> Recorded() =>
        _audit.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Record").Select(c => (AuditEvent)c.GetArguments()[0]!).ToList();

    [Test]
    public void Restore_MovesToPending_AndAudits()
    {
        var source = Write("rom", "gb", "_removed", "a.gb");

        var result = _restorer.Restore([Removed("gb", "a.gb")], Actor);

        Assert.That((result.Restored, result.Failed, result.Code), Is.EqualTo((new[] { new RestoredFile("gb", "a.gb") }, Array.Empty<IngestDeleteRef>(), (string?)null)));
        Assert.That(File.Exists(source), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(_paths.Ingest, "gb", "a.gb")), Is.EqualTo("rom"));
        var row = Recorded().Single();
        Assert.That((row.Action, row.Actor, row.SystemId, row.Path, row.TargetPath),
            Is.EqualTo(("RemovedRestored", Actor, (string?)"gb", (string?)"ingest/gb/_removed/a.gb", (string?)"ingest/gb/a.gb")));
    }

    [Test]
    public void Restore_NeverOverwritesAPendingFile_UsesAFreeName()
    {
        Write("removed", "gb", "_removed", "a.gb");
        var pending = Write("pending", "gb", "A.GB");

        var result = _restorer.Restore([Removed("gb", "a.gb")], Actor);

        Assert.That(result.Restored, Is.EqualTo(new[] { new RestoredFile("gb", "a (2).gb") }));
        Assert.That(File.ReadAllText(pending), Is.EqualTo("pending"));
        Assert.That(File.ReadAllText(Path.Combine(_paths.Ingest, "gb", "a (2).gb")), Is.EqualTo("removed"));
        Assert.That(Recorded().Single().TargetPath, Is.EqualTo("ingest/gb/a (2).gb"));
    }

    [Test]
    public void Restore_MissingSource_IsFailed_AndTheNextFileStillMoves()
    {
        Write("rom", "gb", "_removed", "b.gb");

        var result = _restorer.Restore([Removed("gb", "gone.gb"), Removed("gb", "b.gb")], Actor);

        Assert.That(result.Failed, Is.EqualTo(new[] { Removed("gb", "gone.gb") }));
        Assert.That(result.Restored, Is.EqualTo(new[] { new RestoredFile("gb", "b.gb") }));
        Assert.That(Recorded(), Has.Count.EqualTo(1));
    }

    [Test]
    public void Restore_DuplicatesAreIgnored()
    {
        Write("rom", "gb", "_removed", "a.gb");

        var result = _restorer.Restore([Removed("gb", "a.gb"), Removed("gb", "a.gb")], Actor);

        Assert.That((result.Restored.Count, result.Failed.Count), Is.EqualTo((1, 0)));
        Assert.That(File.Exists(Path.Combine(_paths.Ingest, "gb", "a (2).gb")), Is.False);
    }

    [Test]
    public void Restore_WhenBusy_IsJobRunning_AndMovesNothing()
    {
        var source = Write("rom", "gb", "_removed", "a.gb");

        using (_lock.TryAcquire())
            Assert.That(_restorer.Restore([Removed("gb", "a.gb")], Actor).Code, Is.EqualTo("JobRunning"));

        Assert.That(File.Exists(source), Is.True);
        Assert.That(Recorded(), Is.Empty);
        using var released = _lock.TryAcquire();
        Assert.That(released, Is.Not.Null);
    }

    [Test]
    public void Restore_ExternalSystem_MovesThroughTheCheckedMove_WithAFreeName()
    {
        Write("removed", "ext", "_removed", "a.nsp");
        var pending = Write("pending", "ext", "a.nsp");

        var result = _restorer.Restore([Removed("ext", "a.nsp")], Actor);

        Assert.That(result.Restored, Is.EqualTo(new[] { new RestoredFile("ext", "a (2).nsp") }));
        Assert.That(File.ReadAllText(pending), Is.EqualTo("pending"));
        Assert.That(File.ReadAllText(Path.Combine(_paths.Ingest, "ext", "a (2).nsp")), Is.EqualTo("removed"));
        Assert.That(File.Exists(Path.Combine(_paths.Ingest, "ext", "_removed", "a.nsp")), Is.False);
        Assert.That(Recorded().Single().TargetPath, Is.EqualTo("ingest/ext/a (2).nsp"));
    }

    [Test]
    public void Restore_ExternalLinkedRemovedFolder_IsFailed_AndLeavesTheTargetAlone()
    {
        var outside = Directory.CreateDirectory(_dir.Combine("outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "a.nsp"), "theirs");
        var link = Path.Combine(_paths.Ingest, "ext", "_removed");
        Directory.Delete(link);
        Assert.That(TestLinks.TryLinkDirectory(link, outside), Is.True, "this account can't create a directory link");
        try
        {
            var result = _restorer.Restore([Removed("ext", "a.nsp")], Actor);

            Assert.That((result.Restored.Count, result.Failed.Count), Is.EqualTo((0, 1)));
            Assert.That(File.ReadAllText(Path.Combine(outside, "a.nsp")), Is.EqualTo("theirs"));
            Assert.That(File.Exists(Path.Combine(_paths.Ingest, "ext", "a.nsp")), Is.False);
            Assert.That(Recorded(), Is.Empty);
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(link);
        }
    }
}
