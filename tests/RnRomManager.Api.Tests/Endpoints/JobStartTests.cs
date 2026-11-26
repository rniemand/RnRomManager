using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Endpoints;

/// <summary>D3: a manual start answers 202 with the job id, or 409 JobRunning while the lock is held.</summary>
public class JobStartTests
{
    private JobLock _lock = null!;
    private IJobRepository _jobs = null!;
    private JobRunner _runner = null!;
    private DefaultHttpContext _ctx = null!;

    [SetUp]
    public void SetUp()
    {
        _lock = new JobLock();
        _jobs = Substitute.For<IJobRepository>();
        _runner = new JobRunner(_jobs, _lock, Substitute.For<IUserRepository>(), TimeProvider.System, NullLogger<JobRunner>.Instance);
        _ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7")], "test")),
        };
    }

    [Test]
    public async Task Free_lock_starts_the_job_for_the_caller()
    {
        var job = new NoopTestJob();
        job.Release.SetResult();

        var result = await JobStart.StartManualAsync(_runner, job, _ctx, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<IStatusCodeHttpResult>());
        Assert.That(((IStatusCodeHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status202Accepted));
        var value = ((IValueHttpResult)result).Value!;
        Assert.That(value.GetType().GetProperty("jobId")!.GetValue(value), Is.InstanceOf<Guid>());
        await _jobs.Received(1).InsertAsync(Arg.Is<Job>(j => j.RequestedBy == 7 && j.Trigger == JobTrigger.Manual), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Busy_lock_answers_job_running()
    {
        using var held = _lock.TryAcquire("other", JobType.Integrity);

        var result = await JobStart.StartManualAsync(_runner, new NoopTestJob(), _ctx, CancellationToken.None);

        var problem = (ProblemHttpResult)result;
        Assert.That(problem.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
        Assert.That(problem.ProblemDetails.Extensions["code"], Is.EqualTo("JobRunning"));
        Assert.That(problem.ProblemDetails.Title, Is.EqualTo("Another job is running"));
    }
}
