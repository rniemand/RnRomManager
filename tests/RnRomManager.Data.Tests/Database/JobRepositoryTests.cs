using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Jobs;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

[Category("Integration")]
public class JobRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc);

    private JobRepository _jobs = null!;
    private JobItemRepository _items = null!;

    [SetUp]
    public async Task SetUp()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _jobs = new JobRepository(factory);
        _items = new JobItemRepository(factory);
    }

    private static Job NewJob(JobType type = JobType.Ingest, DateTime? startedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Trigger = JobTrigger.Manual,
        Status = JobStatus.Running,
        StartedAt = startedAt ?? T0,
    };

    private static JobItem NewItem(Guid jobId, string outcome) => new()
    {
        JobId = jobId,
        FileId = Guid.NewGuid(),
        SystemId = "gb",
        SourcePath = "/app/ingest/gb/tetris.gb",
        Outcome = outcome,
        CreatedAt = T0,
    };

    [Test]
    public async Task Insert_Finish_Get_RoundTripsEveryField()
    {
        var job = NewJob(JobType.Integrity) with { Mode = JobMode.Deep, Trigger = JobTrigger.Schedule, StartedAt = T0.AddTicks(1234560) };
        await _jobs.InsertAsync(job, CancellationToken.None);
        Assert.That(await _jobs.GetAsync(job.Id, CancellationToken.None), Is.EqualTo(job));

        await _jobs.FinishAsync(job.Id, JobStatus.Failed, T0.AddMinutes(1), """{"checked":3}""", "boom", CancellationToken.None);

        var finished = await _jobs.GetAsync(job.Id, CancellationToken.None);
        Assert.That(finished, Is.EqualTo(job with { Status = JobStatus.Failed, FinishedAt = T0.AddMinutes(1), Summary = """{"checked": 3}""", Error = "boom" })
            .Or.EqualTo(job with { Status = JobStatus.Failed, FinishedAt = T0.AddMinutes(1), Summary = """{"checked":3}""", Error = "boom" }));
        Assert.That(finished!.FinishedAt!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public async Task Get_Unknown_IsNull() => Assert.That(await _jobs.GetAsync(Guid.NewGuid(), CancellationToken.None), Is.Null);

    [Test]
    public async Task List_NewestFirst_FilteredAndPaged()
    {
        var jobs = Enumerable.Range(0, 5).Select(i => NewJob(i % 2 == 0 ? JobType.Ingest : JobType.DatRefresh, T0.AddMinutes(i))).ToList();
        foreach (var job in jobs)
            await _jobs.InsertAsync(job, CancellationToken.None);

        var all = await _jobs.ListAsync(null, 1, 2, CancellationToken.None);
        Assert.That(all.Total, Is.EqualTo(5));
        Assert.That(all.Items.Select(j => j.Id), Is.EqualTo(new[] { jobs[4].Id, jobs[3].Id }));

        var ingest = await _jobs.ListAsync(JobType.Ingest, 2, 2, CancellationToken.None);
        Assert.That(ingest.Total, Is.EqualTo(3));
        Assert.That(ingest.Items.Select(j => j.Id), Is.EqualTo(new[] { jobs[0].Id }));
    }

    [Test]
    public async Task AbortRunning_OnlyTouchesRunningRows()
    {
        var running = NewJob();
        var done = NewJob();
        await _jobs.InsertAsync(running, CancellationToken.None);
        await _jobs.InsertAsync(done, CancellationToken.None);
        await _jobs.FinishAsync(done.Id, JobStatus.Completed, T0, null, null, CancellationToken.None);

        var aborted = await _jobs.AbortRunningAsync(T0.AddHours(1), CancellationToken.None);

        Assert.That(aborted, Is.EqualTo(new[] { running.Id }));
        Assert.That((await _jobs.GetAsync(running.Id, CancellationToken.None))!.Status, Is.EqualTo(JobStatus.Aborted));
        Assert.That((await _jobs.GetAsync(running.Id, CancellationToken.None))!.FinishedAt, Is.EqualTo(T0.AddHours(1)));
        Assert.That((await _jobs.GetAsync(done.Id, CancellationToken.None))!.Status, Is.EqualTo(JobStatus.Completed));
    }

    [Test]
    public async Task Items_InsertAndList_FilteredByOutcomeInInsertOrder()
    {
        var job = NewJob();
        await _jobs.InsertAsync(job, CancellationToken.None);
        var full = NewItem(job.Id, "Error") with
        {
            ArchiveEntry = "sub/tetris.gb", Reason = "VerifyFailed", Details = "roms/x.gb: SHA1", GameId = Guid.NewGuid(), Sha1 = new string('a', 40),
        };
        var first = await _items.InsertAsync(full, CancellationToken.None);
        await _items.InsertAsync(NewItem(job.Id, "Ingested"), CancellationToken.None);
        await _items.InsertAsync(NewItem(job.Id, "Error"), CancellationToken.None);

        var errors = await _items.ListAsync(job.Id, "Error", 1, 48, CancellationToken.None);
        Assert.That(errors.Total, Is.EqualTo(2));
        Assert.That(errors.Items[0], Is.EqualTo(full with { Id = first }));
        Assert.That((await _items.ListAsync(job.Id, null, 1, 48, CancellationToken.None)).Total, Is.EqualTo(3));
        Assert.That((await _items.ListAsync(Guid.NewGuid(), null, 1, 48, CancellationToken.None)).Total, Is.Zero);
    }
}
