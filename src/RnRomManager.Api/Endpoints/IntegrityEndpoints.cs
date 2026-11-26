using RnRomManager.Api.Auth;
using RnRomManager.Core.Database;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>POST /api/jobs/integrity</c>, <c>GET /api/integrity/status</c> and <c>/issues</c>, and (S4) <c>GET</c>/<c>POST
/// /api/jobs/storage-migration</c>, Admin only (spec §23, §12).
/// </summary>
public static class IntegrityEndpoints
{
    private static readonly string[] Statuses = [IntegrityIssue.Open, IntegrityIssue.AutoRepaired, IntegrityIssue.Resolved];

    public static RouteGroupBuilder MapIntegrityEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/jobs/integrity", async (IntegrityStartRequest request, HttpContext ctx, JobRunner runner, IntegrityJobFactory integrity) =>
        {
            if (request.Mode is not (nameof(JobMode.Quick) or nameof(JobMode.Deep)))
                return Problems.Validation(ctx, "mode", "Must be Quick or Deep.");
            var job = integrity(Enum.Parse<JobMode>(request.Mode));
            return await JobStart.StartManualAsync(runner, job, ctx, ctx.RequestAborted);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // S4, B11: how many games still wait to be moved (either direction), and a manual start (resume) of the storage job.
        api.MapGet("/jobs/storage-migration", async (HttpContext ctx, IGameRepository games, ISystemRegistry systems) =>
            Results.Ok(new
            {
                pendingGames = (await StorageMigrationJob.ListMovesAsync(games, systems, ctx.RequestAborted)).Count,
            })).RequireAuthorization(AuthSetup.AdminOnly);
        api.MapPost("/jobs/storage-migration", async (HttpContext ctx, JobRunner runner, StorageMigrationJobFactory createJob, IGameRepository games,
            ISystemRegistry systems) =>
        {
            if ((await StorageMigrationJob.ListMovesAsync(games, systems, ctx.RequestAborted)).Count == 0)
                return Results.NoContent();
            return await JobStart.StartManualAsync(runner, createJob(), ctx, ctx.RequestAborted);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        var integrityGroup = api.MapGroup("/integrity").RequireAuthorization(AuthSetup.AdminOnly);

        integrityGroup.MapGet("/status", async (HttpContext ctx, IJobRepository jobs, Scheduler scheduler) =>
        {
            var quick = await jobs.GetLatestAsync(JobType.Integrity, JobMode.Quick, ctx.RequestAborted);
            var deep = await jobs.GetLatestAsync(JobType.Integrity, JobMode.Deep, ctx.RequestAborted);
            return Results.Ok(new
            {
                lastQuick = quick is null ? null : JobEndpoints.ToDto(quick),
                lastDeep = deep is null ? null : JobEndpoints.ToDto(deep),
                nextScheduledAt = scheduler.NextQuickCheckAt(),
            });
        });

        integrityGroup.MapGet("/issues", async (string? status, string? type, int? page, int? pageSize, HttpContext ctx,
            IIntegrityIssueRepository issues) =>
        {
            var errors = Paging.Validate(page, pageSize);
            if (status is not null && !Statuses.Contains(status))
                errors["status"] = [$"Must be one of {string.Join(", ", Statuses)}."];
            if (type is not null && !IntegrityIssueType.All.Contains(type))
                errors["type"] = ["Unknown issue type."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await issues.ListAsync(status, type, page ?? 1, pageSize ?? Page<IntegrityIssue>.DefaultSize, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, i => new
            {
                id = i.Id,
                type = i.Type,
                severity = i.Severity,
                system = i.SystemId,
                zipPath = i.ZipPath,
                gameId = i.GameId,
                entryName = i.EntryName,
                details = i.Details,
                status = i.Status,
                firstSeenJobId = i.FirstSeenJobId,
                lastSeenJobId = i.LastSeenJobId,
                detectedAt = i.DetectedAt,
                resolvedAt = i.ResolvedAt,
            }));
        });

        return api;
    }
}
