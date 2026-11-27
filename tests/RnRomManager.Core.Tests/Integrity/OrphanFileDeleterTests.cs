using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Integrity;

public class OrphanFileDeleterTests
{
    private static readonly AuditActor Actor = new(42, "boss", null);
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private JobLock _lock = null!;
    private IIntegrityIssueRepository _issues = null!;
    private IGameRepository _games = null!;
    private IRomRepository _roms = null!;
    private IAuditLog _audit = null!;
    private OrphanFileDeleter _deleter = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Roms = _dir.Combine("roms") };
        Directory.CreateDirectory(Path.Combine(_paths.Roms, "gb"));
        _lock = new JobLock();
        _issues = Substitute.For<IIntegrityIssueRepository>();
        _games = Substitute.For<IGameRepository>();
        _roms = Substitute.For<IRomRepository>();
        _roms.ListExternalAsync(default).ReturnsForAnyArgs([]);
        _audit = Substitute.For<IAuditLog>();
        var time = Substitute.For<TimeProvider>();
        time.GetUtcNow().Returns(new DateTimeOffset(Now));
        _deleter = new OrphanFileDeleter(_lock, _issues, _games, _roms, Options.Create(_paths), _audit, time,
            NullLogger<OrphanFileDeleter>.Instance);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private void Issue(string zipPath, string type = IntegrityIssueType.OrphanFile, string status = IntegrityIssue.Open) =>
        _issues.GetAsync(7, default).ReturnsForAnyArgs(new IntegrityIssue
        {
            Id = 7, Type = type, Severity = "Warn", SystemId = "gb", ZipPath = zipPath, Status = status,
            FirstSeenJobId = Guid.NewGuid(), LastSeenJobId = Guid.NewGuid(), DetectedAt = Now,
        });

    private string Write(string zipPath)
    {
        var path = Path.Combine(_paths.Roms, zipPath);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private List<AuditEvent> Recorded() =>
        _audit.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Record").Select(c => (AuditEvent)c.GetArguments()[0]!).ToList();

    private Task<OrphanDeleteOutcome> Delete() => _deleter.DeleteAsync(7, Actor, default);

    private async Task AssertLeftOpen()
    {
        await _issues.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default, default);
        Assert.That(Recorded(), Is.Empty);
    }

    [Test]
    public async Task OpenOrphan_IsDeleted_Resolved_AndAudited()
    {
        Issue("gb/stray.bin");
        var path = Write("gb/stray.bin");

        Assert.That(await Delete(), Is.EqualTo(OrphanDeleteOutcome.Deleted));

        Assert.That(File.Exists(path), Is.False);
        await _issues.Received(1).ResolveAsync(Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new[] { 7L })), Now, Arg.Any<CancellationToken>());
        var row = Recorded().Single();
        Assert.That((row.Action, row.SystemId, row.Path), Is.EqualTo((AuditActions.OrphanFileDeleted, (string?)"gb", (string?)"roms/gb/stray.bin")));
    }

    [Test]
    public async Task MissingFile_IsResolved_WithoutAnAuditRow()
    {
        Issue("gb/gone.bin");

        Assert.That(await Delete(), Is.EqualTo(OrphanDeleteOutcome.Deleted));

        await _issues.ReceivedWithAnyArgs(1).ResolveAsync(default!, default, default);
        Assert.That(Recorded(), Is.Empty);
    }

    [Test]
    public async Task UnknownIssue_IsNotFound()
    {
        _issues.GetAsync(7, default).ReturnsForAnyArgs((IntegrityIssue?)null);

        Assert.That(await Delete(), Is.EqualTo(OrphanDeleteOutcome.NotFound));
    }

    [TestCase(IntegrityIssueType.ZipUnreadable, IntegrityIssue.Open)]
    [TestCase(IntegrityIssueType.OrphanFile, IntegrityIssue.Resolved)]
    public async Task NotAnOpenOrphan_IsRefused_AndTheFileStays(string type, string status)
    {
        Issue("gb/stray.bin", type, status);
        var path = Write("gb/stray.bin");

        Assert.That(await Delete(), Is.EqualTo(OrphanDeleteOutcome.NotOpenOrphan));
        Assert.That(File.Exists(path), Is.True);
        await AssertLeftOpen();
    }

    [Test]
    public async Task BusyLock_IsJobRunning()
    {
        Issue("gb/stray.bin");
        var path = Write("gb/stray.bin");
        using var held = _lock.TryAcquire("test");

        Assert.That(await Delete(), Is.EqualTo(OrphanDeleteOutcome.JobRunning));
        Assert.That(File.Exists(path), Is.True);
        await AssertLeftOpen();
    }

    [Test]
    public async Task APathNowInTheLibrary_IsNoLongerOrphan()
    {
        Issue("gb/stray.zip");
        var path = Write("gb/stray.zip");
        _games.ZipPathExistsAsync("gb/stray.zip", Arg.Any<CancellationToken>()).Returns(true);

        Assert.That(await Delete(), Is.EqualTo(OrphanDeleteOutcome.NoLongerOrphan));
        Assert.That(File.Exists(path), Is.True);
        await AssertLeftOpen();
    }

    [Test]
    public async Task AnExternalFileNowClaimed_IsNoLongerOrphan()
    {
        Issue("gb/Stray.NSP");
        var path = Write("gb/Stray.NSP");
        _roms.ListExternalAsync(default).ReturnsForAnyArgs([new ExternalRomRow(Guid.NewGuid(), "gb/S/Stray.zip", "x", 1, "s")]);
        Assert.That(await Delete(), Is.Not.EqualTo(OrphanDeleteOutcome.NoLongerOrphan), "another folder's file doesn't count");

        Issue("gb/Stray.NSP");
        Write("gb/Stray.NSP");
        _roms.ListExternalAsync(default).ReturnsForAnyArgs([new ExternalRomRow(Guid.NewGuid(), "gb/Game.zip", "stray.nsp", 1, "s")]);
        Assert.That(await Delete(), Is.EqualTo(OrphanDeleteOutcome.NoLongerOrphan));
        Assert.That(File.Exists(path), Is.True);
    }

    [Test]
    public async Task ADirectory_IsNotDeletable()
    {
        Issue("gb/folder");
        Directory.CreateDirectory(Path.Combine(_paths.Roms, "gb", "folder"));

        Assert.That(await Delete(), Is.EqualTo(OrphanDeleteOutcome.NotDeletable));
        Assert.That(Directory.Exists(Path.Combine(_paths.Roms, "gb", "folder")), Is.True);
        await AssertLeftOpen();
    }

    [Test]
    public async Task APathOutsideTheRoot_IsNotDeletable()
    {
        Issue("../outside.bin");
        File.WriteAllBytes(_dir.Combine("outside.bin"), [1]);

        Assert.That(await Delete(), Is.EqualTo(OrphanDeleteOutcome.NotDeletable));
        Assert.That(File.Exists(_dir.Combine("outside.bin")), Is.True);
        await AssertLeftOpen();
    }

    [Test]
    public async Task ALink_IsNotDeletable()
    {
        Issue("gb/link.bin");
        var target = Write("gb/target.bin");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_paths.Roms, "gb", "link.bin"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Ignore("Creating a symbolic link needs a privilege this machine lacks.");
        }

        Assert.That(await Delete(), Is.EqualTo(OrphanDeleteOutcome.NotDeletable));
        Assert.That(File.Exists(target), Is.True);
        await AssertLeftOpen();
    }

    [Test]
    [Platform("Win", Reason = "Only Windows refuses to delete a file another handle holds open.")]
    public async Task AFailingDelete_IsDeleteFailed()
    {
        Issue("gb/locked.bin");
        var path = Write("gb/locked.bin");
        await using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.That(await Delete(), Is.EqualTo(OrphanDeleteOutcome.DeleteFailed));
        await AssertLeftOpen();
    }
}
