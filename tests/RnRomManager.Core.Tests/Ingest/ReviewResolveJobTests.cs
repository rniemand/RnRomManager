using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Spec B2–B5: a Review resolution run as a job through <see cref="JobRunner"/>, which holds the lock.</summary>
public class ReviewResolveJobTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] Rom = TestLibrary.RomBytes(5);
    private static readonly ReviewResolutionRequest Manual = new() { Action = "Manual", Title = "My Game", Regions = [], Languages = [], Flags = [] };

    private TempDir _dir = null!;
    private PathsOptions _paths = null!;
    private IIngestReviewRepository _reviews = null!;
    private IJobRepository _jobs = null!;
    private IJobItemRepository _items = null!;
    private ReviewItem _item = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        _paths = new PathsOptions { Ingest = _dir.Combine("ingest"), Roms = _dir.Combine("roms"), Cache = _dir.Combine("cache") };
        var ingest = Directory.CreateDirectory(Path.Combine(_paths.Ingest, ExternalTestHandler.SystemId)).FullName;
        _item = new ReviewItem
        {
            Id = 1, SystemId = ExternalTestHandler.SystemId, Status = "Pending", Reason = "NoDatMatch",
            SourcePath = Path.Combine(ingest, "game.nsp"), OriginalFileName = "game.nsp", SizeBytes = Rom.Length,
            Sha1 = Convert.ToHexStringLower(SHA1.HashData(Rom)), Crc32 = Crc32.ToHex(Rom), Md5 = Convert.ToHexStringLower(MD5.HashData(Rom)),
        };
        File.WriteAllBytes(_item.SourcePath, Rom);
        _reviews = Substitute.For<IIngestReviewRepository>();
        _reviews.GetAsync(1, Arg.Any<CancellationToken>()).Returns(_ => _item);
        _jobs = Substitute.For<IJobRepository>();
        _items = Substitute.For<IJobItemRepository>();
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private (JobRunner Runner, ReviewResolveJob Job) Build(ExternalIngestHooks? hooks = null)
    {
        var systems = new SystemRegistry([new ExternalTestHandler()]);
        var options = Options.Create(_paths);
        var dats = new DatCache(options, systems, NullLogger<DatCache>.Instance);
        var games = Substitute.For<IGameRepository>();
        // Every candidate ZIP path is free: each path is its own group with no owner.
        games.ListZipPathEquivalenceAsync(default!, default).ReturnsForAnyArgs(c =>
            c.Arg<IReadOnlyList<string>>().Select(path => new ZipPathEquivalenceGroup([path], [])).ToList());
        var roms = Substitute.For<IRomRepository>();
        roms.ListGameIdsByBaseTitleAsync(default!, default!, default).ReturnsForAnyArgs([]);
        roms.ListExternalAsync(default).ReturnsForAnyArgs([]);
        var reader = new InfoJsonReader([], systems);
        var audit = Substitute.For<IAuditLog>();
        var pipeline = new IngestPipeline(dats, systems, roms, games, Substitute.For<ILibraryMirror>(), Substitute.For<IIntegrityIssueRepository>(),
            _reviews, reader, TestMedia.Offline(), options, audit, new AuditPaths(options), TimeProvider.System, NullLogger<IngestPipeline>.Instance)
        {
            ExternalHooks = hooks,
        };
        var jobLock = new JobLock();
        var resolver = new ReviewResolver(jobLock, _reviews, games, roms,
            new DbSync(games, roms, Substitute.For<ILibraryMirror>(), reader, systems, options, NullLogger<DbSync>.Instance), pipeline, dats, systems,
            new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance),
            new ReviewSets(new IngestFileScanner(options, systems, NullLogger<IngestFileScanner>.Instance), _reviews, systems, options, NullLogger<ReviewSets>.Instance),
            options, audit, new AuditPaths(options), TimeProvider.System,
            NullLogger<ReviewResolver>.Instance);
        var runner = new JobRunner(_jobs, jobLock, Substitute.For<IUserRepository>(), TimeProvider.System, NullLogger<JobRunner>.Instance);
        var job = new ReviewResolveJob(resolver, _items, TimeProvider.System, NullLogger<ReviewResolveJob>.Instance)
        {
            ReviewId = 1, FileName = "game.nsp", Request = Manual,
        };
        return (runner, job);
    }

    /// <summary>Runs the job to its end; returns its summary as recorded by the runner.</summary>
    private async Task<JsonElement> RunAsync(JobRunner runner, ReviewResolveJob job, JobStatus expected = JobStatus.Completed)
    {
        var started = await runner.StartAsync(job, JobTrigger.Manual, 42, CancellationToken.None);
        Assert.That(started, Is.Not.Null);
        await started!.Completion.WaitAsync(Timeout);
        var finish = _jobs.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IJobRepository.FinishAsync)).GetArguments();
        Assert.That(finish[1], Is.EqualTo(expected), (string?)finish[4]);
        return JsonDocument.Parse((string)finish[3]!).RootElement;
    }

    private List<JobItem> Items() => _items.ReceivedCalls().Select(c => (JobItem)c.GetArguments()[0]!).ToList();

    [Test]
    public async Task Success_UnderTheRunnersLock_RecordsTheGameAndAnIngestedItem()
    {
        var (runner, job) = Build();

        var summary = await RunAsync(runner, job);

        Assert.That(summary.GetProperty("code").ValueKind, Is.EqualTo(JsonValueKind.Null), "not JobRunning: the resolver doesn't take the lock again");
        var gameId = summary.GetProperty("gameId").GetGuid();
        Assert.That(summary.GetProperty("reviewId").GetInt64(), Is.EqualTo(1));
        Assert.That(summary.GetProperty("fileName").GetString(), Is.EqualTo("game.nsp"));
        Assert.That(summary.GetProperty("errors").ValueKind, Is.EqualTo(JsonValueKind.Null));
        var item = Items().Single();
        Assert.That((item.Outcome, item.Reason, item.GameId, item.SourcePath, item.Sha1, item.SystemId),
            Is.EqualTo((IngestOutcome.Ingested, (string?)null, (Guid?)gameId, _item.SourcePath, _item.Sha1, ExternalTestHandler.SystemId)));
        await _reviews.Received(1).SetResolutionAsync(1, "Resolved", gameId, 42, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VerifyFailed_KeepsPending_WithAnErrorItem()
    {
        File.WriteAllBytes(_item.SourcePath, [1, 2, 3]);
        var (runner, job) = Build();

        var summary = await RunAsync(runner, job);

        Assert.That(summary.GetProperty("code").GetString(), Is.EqualTo("VerifyFailed"));
        Assert.That(summary.GetProperty("gameId").ValueKind, Is.EqualTo(JsonValueKind.Null));
        var item = Items().Single();
        Assert.That((item.Outcome, item.Reason), Is.EqualTo((IngestOutcome.Error, "VerifyFailed")));
        await _reviews.DidNotReceiveWithAnyArgs().SetResolutionAsync(default(long), default!, default, default, default, default);
        Assert.That(File.Exists(_item.SourcePath), Is.True);
    }

    [Test]
    public async Task SourceMissing_RemovesTheRow()
    {
        File.Delete(_item.SourcePath);
        var (runner, job) = Build();

        var summary = await RunAsync(runner, job);

        Assert.That(summary.GetProperty("code").GetString(), Is.EqualTo("SourceMissing"));
        await _reviews.Received(1).DeleteAsync(Arg.Is<IReadOnlyCollection<long>>(ids => ids.Single() == 1), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ItemNoLongerPending_IsNotFound_WithTheJobsFileName_AndNoItem()
    {
        _item = _item with { Status = "Resolved" };
        var (runner, job) = Build();

        var summary = await RunAsync(runner, job);

        Assert.That(summary.GetProperty("code").GetString(), Is.EqualTo("NotFound"));
        Assert.That(summary.GetProperty("fileName").GetString(), Is.EqualTo("game.nsp"));
        Assert.That(Items(), Is.Empty);
    }

    [Test]
    public async Task ExternalResolve_ReportsHashingThenCopyingToLibrary()
    {
        JobProgress? progress = null;
        var phases = new List<string?>();
        void Observe() => phases.Add(progress?.StepAt(DateTime.UtcNow)?.Phase);
        var (runner, job) = Build(new ExternalIngestHooks
        {
            WrapHashStream = stream => new ObservedStream(stream, Observe),
            Move = new ExternalMoveHooks { Rename = (from, to) => NoReplaceRename.FromErrno(95, from, to), WrapSource = stream => new ObservedStream(stream, Observe) },
        });
        var started = await runner.StartAsync(new Capture(job, p => progress = p), JobTrigger.Manual, 42, CancellationToken.None);
        await started!.Completion.WaitAsync(Timeout);

        Assert.That(phases.Distinct().ToList(), Is.EqualTo(new[] { JobStepPhases.Hashing, JobStepPhases.CopyingToLibrary }));
        Assert.That(progress!.Current, Is.EqualTo(1));
        Assert.That(progress.Total, Is.EqualTo(1));
    }

    [Test]
    public async Task FailingJobItemInsert_StillCompletesWithTheSummary()
    {
        _items.InsertAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));
        var (runner, job) = Build();

        var summary = await RunAsync(runner, job);

        Assert.That(summary.GetProperty("gameId").ValueKind, Is.EqualTo(JsonValueKind.String));
    }

    [Test]
    public void IsNotCancellable()
    {
        var (_, job) = Build();
        Assert.That(((IJob)job).Cancellable, Is.False);
        Assert.That((job.Type, job.Mode), Is.EqualTo((JobType.ReviewResolve, (JobMode?)null)));
    }

    /// <summary>Hands the test the job's progress, then runs the job.</summary>
    private sealed class Capture(IJob inner, Action<JobProgress> seen) : IJob
    {
        public JobType Type => inner.Type;
        public JobMode? Mode => inner.Mode;

        public Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
        {
            seen(context.Progress);
            return inner.RunAsync(context, cancellationToken);
        }
    }

    private sealed class ObservedStream(Stream inner, Action observe) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            observe();
            return inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            observe();
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
