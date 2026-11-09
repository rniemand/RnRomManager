using System.Text.Json;
using Microsoft.Extensions.Options;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>POST /api/jobs/ingest</c>, <c>GET /api/ingest/pending</c>, <c>GET /api/ingest/failed</c> and <c>POST /api/ingest/delete</c>, Admin only (spec §23, §7.1).</summary>
public static class IngestEndpoints
{
    public static RouteGroupBuilder MapIngestEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/jobs/ingest", async (HttpContext ctx, JobRunner runner, IngestJob job) =>
        {
            // IN1: no body or files null → every pending file; otherwise only the listed ones. Read by hand: a body
            // parameter would make the route require a JSON content type, and an empty POST must still work.
            IngestRequest? request = null;
            if (ctx.Request.ContentLength is not 0 && ctx.Request.HasJsonContentType())
            {
                try
                {
                    request = await ctx.Request.ReadFromJsonAsync<IngestRequest>(ctx.RequestAborted);
                }
                catch (JsonException)
                {
                    return Problems.Validation(ctx, new Dictionary<string, string[]> { ["files"] = ["The body isn't valid JSON."] });
                }
            }
            if (request?.Files is { } files)
            {
                if (files.Count == 0 || files.Any(f => string.IsNullOrEmpty(f?.System) || string.IsNullOrEmpty(f.Name)))
                    return Problems.Validation(ctx, new Dictionary<string, string[]>
                    {
                        ["files"] = ["Give at least one file, each with a system and a name."],
                    });
                job.Selection = files.Select(f => (f!.System!, f.Name!)).ToHashSet();
            }
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

        api.MapGet("/ingest/failed", (IngestFileScanner scanner) => Results.Ok(scanner.ScanFailed().Select(s => new
        {
            system = s.SystemId,
            files = s.Files.Select(f => new { name = f.Name, sizeBytes = f.SizeBytes, modifiedAt = f.ModifiedAtUtc, reason = f.Reason }),
        }))).RequireAuthorization(AuthSetup.AdminOnly);

        api.MapPost("/ingest/delete", async (HttpContext ctx, IngestFileDeleter deleter, ISystemRegistry systems, IOptions<PathsOptions> paths) =>
        {
            // D2: read by hand, as /jobs/ingest does, so a missing or non-JSON body is a 400 with errors.files.
            IngestDeleteRequest? request = null;
            if (ctx.Request.ContentLength is not 0 && ctx.Request.HasJsonContentType())
            {
                try
                {
                    request = await ctx.Request.ReadFromJsonAsync<IngestDeleteRequest>(ctx.RequestAborted);
                }
                catch (JsonException)
                {
                    return Problems.Validation(ctx, new Dictionary<string, string[]> { ["files"] = ["The body isn't valid JSON."] });
                }
            }
            var errors = IngestFileDeleter.Validate(request?.Files, systems, paths.Value.Ingest);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await deleter.DeleteAsync(request!.Files!.Select(f => f!).ToList(), AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null
                ? Results.Ok(new { deleted = result.Deleted, failed = result.Failed.Select(f => new { system = f.System, folder = f.Folder, name = f.Name }) })
                : DatEndpoints.JobRunning(ctx);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        return api;
    }
}

/// <summary><c>POST /api/ingest/delete</c> body (D2).</summary>
public sealed record IngestDeleteRequest(IReadOnlyList<IngestDeleteRef?>? Files);

/// <summary><c>POST /api/jobs/ingest</c> body (IN1): the pending files to ingest, by system id and top-level file name.</summary>
public sealed record IngestRequest(IReadOnlyList<IngestFileRef?>? Files);

public sealed record IngestFileRef(string? System, string? Name);
