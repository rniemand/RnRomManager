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

public class IngestFileDeleterTests
{
    private static readonly AuditActor Actor = new(42, "boss", null);
    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private SystemRegistry _systems = null!;
    private JobLock _lock = null!;
    private IIngestReviewRepository _reviews = null!;
    private IAuditLog _audit = null!;
    private IngestFileDeleter _deleter = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Ingest = _dir.Combine("ingest") };
        Directory.CreateDirectory(Path.Combine(_paths.Ingest, "gb", "_failed"));
        var gb = Substitute.For<ISystemHandler>();
        gb.Id.Returns("gb");
        _systems = new SystemRegistry([gb]);
        _lock = new JobLock();
        _reviews = Substitute.For<IIngestReviewRepository>();
        _audit = Substitute.For<IAuditLog>();
        var options = Options.Create(_paths);
        _deleter = new IngestFileDeleter(_lock, _reviews, options, _audit, new AuditPaths(options), NullLogger<IngestFileDeleter>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private string Write(params string[] relative)
    {
        var path = Path.Combine([_paths.Ingest, .. relative]);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private List<AuditEvent> Recorded() =>
        _audit.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Record").Select(c => (AuditEvent)c.GetArguments()[0]!).ToList();

    private Dictionary<string, string[]> Validate(params IngestDeleteRef?[] files) => IngestFileDeleter.Validate(files, _systems, _paths.Ingest);

    [Test]
    public void Validate_AcceptsPendingAndFailedEntries() =>
        Assert.That(Validate(new("gb", "pending", "a.gb"), new("gb", "failed", "b.gb")), Is.Empty);

    [TestCase(null, "pending", "a.gb")]
    [TestCase("nes", "pending", "a.gb")]
    [TestCase("GB", "pending", "a.gb")]
    [TestCase("gb", "removed", "a.gb")]
    [TestCase("gb", null, "a.gb")]
    [TestCase("gb", "pending", null)]
    [TestCase("gb", "pending", "")]
    [TestCase("gb", "pending", ".")]
    [TestCase("gb", "pending", "..")]
    [TestCase("gb", "pending", "../x.gb")]
    [TestCase("gb", "pending", "sub\\x.gb")]
    [TestCase("gb", "pending", ".hidden")]
    [TestCase("gb", "pending", "Thumbs.db")]
    [TestCase("gb", "failed", "a.gb.ERROR.txt")]
    public void Validate_RejectsBadEntries(string? system, string? folder, string? name) =>
        Assert.That(Validate(new("gb", "pending", "ok.gb"), new(system, folder, name))["files"], Has.Length.EqualTo(1).And.One.StartsWith("files[1]:"));

    [Test]
    public void Validate_PendingMayDeleteAnErrorTxtName() =>
        Assert.That(Validate(new IngestDeleteRef("gb", "pending", "a.error.txt")), Is.Empty);

    [Test]
    public void Validate_RejectsNullEmptyAndOversizedLists()
    {
        Assert.That(IngestFileDeleter.Validate(null, _systems, _paths.Ingest), Contains.Key("files"));
        Assert.That(Validate(), Contains.Key("files"));
        Assert.That(Validate((IngestDeleteRef?)null), Contains.Key("files"));
        var many = Enumerable.Range(0, 501).Select(i => (IngestDeleteRef?)new IngestDeleteRef("gb", "pending", $"{i}.gb")).ToArray();
        Assert.That(Validate(many), Contains.Key("files"));
        Assert.That(Validate(many[..500]), Is.Empty);
    }

    [Test]
    public async Task BusyLock_IsJobRunning_AndDeletesNothing()
    {
        var path = Write("gb", "a.gb");
        using (_lock.TryAcquire())
            Assert.That((await _deleter.DeleteAsync([new("gb", "pending", "a.gb")], Actor, default)).Code, Is.EqualTo("JobRunning"));
        Assert.That(File.Exists(path), Is.True);
        await _reviews.DidNotReceiveWithAnyArgs().DeletePendingBySourceAsync(default!, default, default);
        Assert.That(Recorded(), Is.Empty);
    }

    [Test]
    public async Task Pending_DeletesFile_AndItsPendingReviewRows()
    {
        var loose = Write("gb", "a.gb");
        var zip = Write("gb", "b.ZIP");

        var result = await _deleter.DeleteAsync([new("gb", "pending", "a.gb"), new("gb", "pending", "b.ZIP")], Actor, default);

        Assert.That(result, Has.Property("Deleted").EqualTo(2).And.Property("Failed").Empty.And.Property("Code").Null);
        Assert.That(File.Exists(loose) || File.Exists(zip), Is.False);
        await _reviews.Received(1).DeletePendingBySourceAsync(loose, null, Arg.Any<CancellationToken>());
        await _reviews.Received(1).DeletePendingBySourceAsync(zip, zip, Arg.Any<CancellationToken>());
        using var released = _lock.TryAcquire();
        Assert.That(released, Is.Not.Null);
    }

    [Test]
    public async Task Failed_DeletesFileAndSidecar_WithoutTouchingReviews()
    {
        var path = Write("gb", "_failed", "a.gb");
        var sidecar = Write("gb", "_failed", "a.gb.error.txt");

        var result = await _deleter.DeleteAsync([new("gb", "failed", "a.gb")], Actor, default);

        Assert.That(result.Deleted, Is.EqualTo(1));
        Assert.That(File.Exists(path) || File.Exists(sidecar), Is.False);
        await _reviews.DidNotReceiveWithAnyArgs().DeletePendingBySourceAsync(default!, default, default);
    }

    [Test]
    public async Task MissingFile_CountsAsDeleted_WithoutAudit_AndStillClearsReviewRows()
    {
        var result = await _deleter.DeleteAsync([new("gb", "pending", "gone.gb"), new("gb", "failed", "gone.gb")], Actor, default);

        Assert.That(result.Deleted, Is.EqualTo(2));
        Assert.That(Recorded(), Is.Empty);
        await _reviews.Received(1).DeletePendingBySourceAsync(Path.Combine(_paths.Ingest, "gb", "gone.gb"), null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RepeatedEntries_AreSkipped()
    {
        Write("gb", "a.gb");
        var result = await _deleter.DeleteAsync([new("gb", "pending", "a.gb"), new("gb", "pending", "a.gb")], Actor, default);
        Assert.That(result.Deleted, Is.EqualTo(1));
        await _reviews.ReceivedWithAnyArgs(1).DeletePendingBySourceAsync(default!, default, default);
    }

    [Test]
    public async Task FailingDelete_IsReported_KeepsItsRows_AndTheNextFileIsStillDeleted()
    {
        // A directory where the file should be: File.Delete refuses on every OS (a locked file only blocks on Windows).
        var blocked = Path.Combine(_paths.Ingest, "gb", "blocked.gb");
        Directory.CreateDirectory(blocked);
        var next = Write("gb", "next.gb");

        var result = await _deleter.DeleteAsync([new("gb", "pending", "blocked.gb"), new("gb", "pending", "next.gb")], Actor, default);

        Assert.That(result.Deleted, Is.EqualTo(1));
        Assert.That(result.Failed, Is.EqualTo(new[] { new IngestDeleteRef("gb", "pending", "blocked.gb") }));
        Assert.That(File.Exists(next), Is.False);
        await _reviews.DidNotReceive().DeletePendingBySourceAsync(blocked, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailingFailedDelete_KeepsItsSidecar()
    {
        Directory.CreateDirectory(Path.Combine(_paths.Ingest, "gb", "_failed", "a.gb"));
        var sidecar = Write("gb", "_failed", "a.gb.error.txt");

        var result = await _deleter.DeleteAsync([new("gb", "failed", "a.gb")], Actor, default);

        Assert.That(result.Failed, Has.Count.EqualTo(1));
        Assert.That(File.Exists(sidecar), Is.True);
    }

    [Test]
    public async Task Audit_RecordsActionSystemPathAndFolder()
    {
        Write("gb", "a.gb");
        Write("gb", "_failed", "b.gb");

        await _deleter.DeleteAsync([new("gb", "pending", "a.gb"), new("gb", "failed", "b.gb")], Actor, default);

        Assert.That(Recorded().Select(e => (e.Action, e.Actor, e.SystemId, e.Path, e.Details)), Is.EqualTo(new[]
        {
            (AuditActions.IngestFileDeleted, Actor, (string?)"gb", (string?)"ingest/gb/a.gb", (string?)"pending"),
            (AuditActions.IngestFileDeleted, Actor, "gb", "ingest/gb/_failed/b.gb", "failed"),
        }));
    }
}
