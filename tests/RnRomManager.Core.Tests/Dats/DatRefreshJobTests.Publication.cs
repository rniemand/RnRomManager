using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Dats;

/// <summary>Spec T2: publication through <c>cache/dats.previous/</c>, its recovery, and the refresh's work folder.</summary>
public partial class DatRefreshJobTests
{
    private string Live => DatPublication.LivePath(_paths.Cache);
    private string Previous => DatPublication.PreviousPath(_paths.Cache);
    private string WorkBase => Path.Combine(_paths.Cache, CrashRecovery.WorkFolder);

    private DatRefreshJob Job(StubHttpHandler server, DatPublicationHooks hooks)
    {
        var job = Job(server);
        job.PublicationHooks = hooks;
        return job;
    }

    private static string? ManifestVersion(string datsFolder) =>
        DatManifest.Read(Path.Combine(datsFolder, DatManifest.FileName)).First(e => e.Version is not null).Version;

    private bool Recover() => DatPublication.RecoverAtStartup(_paths.Cache, NullLogger.Instance);

    /// <summary>A move that fails when <paramref name="fails"/> says so, and otherwise moves.</summary>
    private static DatPublicationHooks FailMove(Func<string, string, bool> fails) => new()
    {
        Move = (from, to) =>
        {
            if (fails(from, to))
                throw new IOException($"refused: {from} → {to}");
            Directory.Move(from, to);
        },
    };

    [Test]
    public async Task FailureBetweenRenames_PutsTheOldSetBack()
    {
        WriteLiveV1();
        var hooks = FailMove((from, _) => from.StartsWith(WorkBase, StringComparison.Ordinal)); // staged → live fails

        await Assert.ThrowsAsync<IOException>(() => Job(Server(), hooks).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default));

        Assert.That(Directory.Exists(Previous), Is.False);
        AssertStillV1();
    }

    [Test]
    public async Task FailedRollback_KeepsTheBackup_AndRestartRestoresIt()
    {
        WriteLiveV1();
        // Both the staged → live rename and the old set's way back fail.
        var hooks = FailMove((_, to) => to == Live);

        await Assert.ThrowsAsync<IOException>(() => Job(Server(), hooks).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default));

        Assert.That(Directory.Exists(Live), Is.False);
        Assert.That(ManifestVersion(Previous), Is.EqualTo("v1"), "the prior cache survives in the backup");

        Assert.That(Recover(), Is.True);
        Assert.That(Directory.Exists(Previous), Is.False);
        AssertStillV1();
    }

    [Test]
    public void Restart_WithBothSets_KeepsLive_AndRemovesTheBackup()
    {
        WriteLiveV1();
        FixtureDatWriter.Write(Previous, Games(), version: "v0");

        Assert.That(Recover(), Is.True);

        Assert.That(Directory.Exists(Previous), Is.False);
        AssertStillV1();
    }

    [Test]
    public void Restart_WithNeither_DoesNothing()
    {
        Assert.That(Recover(), Is.True);
        Assert.That(Directory.Exists(Live) || Directory.Exists(Previous), Is.False);
    }

    /// <summary>A directory link at <paramref name="link"/> to a new outside folder holding one file.</summary>
    private string LinkOutside(string link)
    {
        var outside = Directory.CreateDirectory(_dir.Combine("outside-" + Path.GetFileName(link))).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TestLinks.TryLinkDirectory(link, outside))
            Assert.Ignore("this account can't create a directory link");
        return outside;
    }

    [Test]
    public async Task LinkedBackup_IsNeverDeleted_AndBlocksARefresh()
    {
        WriteLiveV1();
        var outside = LinkOutside(Previous);
        try
        {
            Assert.That(Recover(), Is.True, "live is fine, so it loads");
            await Assert.ThrowsAsync<IOException>(() => RunAsync(Server()));

            Assert.That(File.Exists(Path.Combine(outside, "keep.txt")), Is.True);
            Assert.That(new DirectoryInfo(Previous).Attributes.HasFlag(FileAttributes.ReparsePoint), Is.True, "the link itself stays");
            AssertStillV1();
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(Previous);
        }
    }

    [Test]
    public void LinkedLive_IsNotLoaded_AndTheBackupIsKept()
    {
        FixtureDatWriter.Write(Previous, Games(), version: "v1");
        var outside = LinkOutside(Live);
        try
        {
            Assert.That(Recover(), Is.False, "an unsafe live set is not loaded");

            Assert.That(ManifestVersion(Previous), Is.EqualTo("v1"), "the backup is never taken as proof of a commit");
            Assert.That(File.Exists(Path.Combine(outside, "keep.txt")), Is.True);
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(Live);
        }
    }

    [Test]
    public async Task LinkedWorkFolder_FailsBeforeWriting()
    {
        WriteLiveV1();
        var outside = LinkOutside(WorkBase);
        var server = Server();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => RunAsync(server));

            Assert.That(server.Requests, Is.Empty);
            Assert.That(Directory.GetFileSystemEntries(outside), Is.EqualTo(new[] { Path.Combine(outside, "keep.txt") }));
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(WorkBase);
        }
        AssertStillV1();
    }

    [Test]
    public async Task OccupiedJobFolder_FailsBeforeWriting_AndIsLeftAlone()
    {
        WriteLiveV1();
        var id = Guid.NewGuid();
        var occupant = Directory.CreateDirectory(Path.Combine(WorkBase, id.ToString())).FullName;
        File.WriteAllText(Path.Combine(occupant, "keep.txt"), "x");
        var server = Server();

        await Assert.ThrowsAsync<IOException>(() => Job(server).RunAsync(new JobContext(id, new JobProgress()), default));

        Assert.That(server.Requests, Is.Empty);
        Assert.That(File.ReadAllText(Path.Combine(occupant, "keep.txt")), Is.EqualTo("x"));
    }

    [Test]
    public async Task CleanupFailure_LeavesTheCommittedSetActive()
    {
        WriteLiveV1();
        var hooks = new DatPublicationHooks { Delete = path => throw new IOException($"refused: {path}") };

        var result = await Job(Server(), hooks).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);

        Assert.That(result, Is.Not.Null);
        Assert.That(_cache.For("gb")!.Version, Is.EqualTo("v2"));
        Assert.That(ManifestVersion(Live), Is.EqualTo("v2"));
        Assert.That(ManifestVersion(Previous), Is.EqualTo("v1"), "kept; the next refresh or restart removes it");
        _audit.ReceivedWithAnyArgs(1).Record(default!);
    }

    [Test]
    public async Task CancelAfterCommit_EndsCancelled_WithTheNewSetActive()
    {
        WriteLiveV1();
        var jobs = Substitute.For<IJobRepository>();
        var runner = new JobRunner(jobs, new JobLock(), Substitute.For<RnRomManager.Core.Users.IUserRepository>(), TimeProvider.System,
            NullLogger<JobRunner>.Instance);
        var hooks = new DatPublicationHooks
        {
            Move = (from, to) =>
            {
                Directory.Move(from, to);
                if (to == Live)
                    runner.Cancel(runner.Current!.Id); // right after the commit
            },
        };

        var started = await runner.StartAsync(Job(Server(), hooks), JobTrigger.Manual, null, CancellationToken.None);
        await started!.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        await jobs.Received(1).FinishAsync(started.Id, JobStatus.Cancelled, Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        Assert.That(_cache.For("gb")!.Version, Is.EqualTo("v2"));
        Assert.That(ManifestVersion(Live), Is.EqualTo("v2"));
    }
}
