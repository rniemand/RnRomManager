using System.Text.Json.Nodes;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Database;
using RnRomManager.Core.Jobs;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>/api/jobs*</c> read + cancel, Admin only (spec §23, §3.1). Job start endpoints come with each job.</summary>
public static class JobEndpoints
{
    public static RouteGroupBuilder MapJobEndpoints(this RouteGroupBuilder api)
    {
        // L2: what holds the job lock, for every signed-in user's footer.
        api.MapGet("/activity", (JobLock jobLock) => jobLock.Activity is { } held
            ? Results.Ok(new { activity = held.Activity, jobType = held.JobType?.ToString() })
            : Results.NoContent());

        var jobs = api.MapGroup("/jobs").RequireAuthorization(AuthSetup.AdminOnly);

        jobs.MapGet("/current", (JobRunner runner) =>
            runner.Current is { } job ? Results.Ok(job) : Results.NoContent());

        jobs.MapGet("", async (string? type, int? page, int? pageSize, HttpContext ctx, IJobRepository repository) =>
        {
            var errors = Paging.Validate(page, pageSize);
            JobType? jobType = null;
            if (type is not null)
            {
                if (Enum.TryParse<JobType>(type, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
                    jobType = parsed;
                else
                    errors["type"] = [$"Must be one of {string.Join(", ", Enum.GetNames<JobType>())}."];
            }
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var result = await repository.ListAsync(jobType, page ?? 1, pageSize ?? Page<Job>.DefaultSize, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, ToDto));
        });

        jobs.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, IJobRepository repository) =>
            await repository.GetAsync(id, ctx.RequestAborted) is { } job ? Results.Ok(ToDto(job)) : Problems.NotFound(ctx));

        jobs.MapGet("/{id:guid}/items", async (Guid id, string? outcome, int? page, int? pageSize, HttpContext ctx,
            IJobRepository repository, IJobItemRepository items) =>
        {
            var errors = Paging.Validate(page, pageSize);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            if (await repository.GetAsync(id, ctx.RequestAborted) is null)
                return Problems.NotFound(ctx);

            var result = await items.ListAsync(id, outcome, page ?? 1, pageSize ?? Page<JobItem>.DefaultSize, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, item => item));
        });

        jobs.MapPost("/{id:guid}/cancel", (Guid id, HttpContext ctx, JobRunner runner) =>
            runner.Cancel(id) switch
            {
                CancelResult.Cancelled => Results.Accepted(),
                CancelResult.NotCancellable => Problems.Create(ctx, StatusCodes.Status409Conflict, "NotCancellable", "This job can't be cancelled."),
                _ => Problems.NotFound(ctx),
            });

        return api;
    }

    public static object ToDto(Job job) => new
    {
        id = job.Id,
        type = job.Type,
        mode = job.Mode,
        triggerSource = job.Trigger,
        status = job.Status,
        requestedBy = job.RequestedBy,
        startedAt = job.StartedAt,
        finishedAt = job.FinishedAt,
        summary = job.Summary is null ? null : JsonNode.Parse(job.Summary),
        error = job.Error,
    };
}
