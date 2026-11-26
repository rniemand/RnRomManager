using RnRomManager.Core.Jobs;

namespace RnRomManager.Api.Endpoints;

/// <summary>Starts a job by hand for the caller: <c>202 { jobId }</c>, or <c>409 JobRunning</c> when the job lock is busy.</summary>
public static class JobStart
{
    public static async Task<IResult> StartManualAsync(
        JobRunner runner,
        IJob job,
        HttpContext ctx,
        CancellationToken ct)
    {
        var started = await runner.StartAsync(job, JobTrigger.Manual, EndpointUser.Id(ctx.User), ct);
        return started is null ? Running(ctx) : Results.Accepted(value: new { jobId = started.Id });
    }

    public static IResult Running(HttpContext ctx) =>
        Problems.Create(ctx, StatusCodes.Status409Conflict, "JobRunning", "Another job is running");
}
