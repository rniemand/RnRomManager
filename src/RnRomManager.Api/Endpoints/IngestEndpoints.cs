using RnRomManager.Api.Auth;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>POST /api/jobs/ingest</c> and <c>GET /api/ingest/pending</c>, Admin only (spec §23, §7.1).</summary>
public static class IngestEndpoints
{
    public static RouteGroupBuilder MapIngestEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/jobs/ingest", async (HttpContext ctx, JobRunner runner, IngestJob job) =>
        {
            try
            {
                var started = await runner.StartAsync(job, JobTrigger.Manual, DatEndpoints.UserId(ctx.User), ctx.RequestAborted);
                return started is null ? DatEndpoints.JobRunning(ctx) : Results.Accepted(value: new { jobId = started.Id });
            }
            catch (JobRefusedException ex) when (ex.Code == IngestJob.DatsMissing)
            {
                return Problems.Create(ctx, StatusCodes.Status409Conflict, ex.Code, "DATs aren't loaded for every system; refresh DATs first");
            }
        }).RequireAuthorization(AuthSetup.AdminOnly);

        api.MapGet("/ingest/pending", (IngestFileScanner scanner) => Results.Ok(scanner.Scan().Select(s => new
        {
            system = s.SystemId,
            files = s.Files.Select(f => new { name = f.Name, sizeBytes = f.SizeBytes, modifiedAt = f.ModifiedAtUtc }),
        }))).RequireAuthorization(AuthSetup.AdminOnly);

        return api;
    }
}
