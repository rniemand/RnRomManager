using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RnRomManager.Api.Auth;
using RnRomManager.Api.Http;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>POST /api/jobs/dat-refresh</c> and <c>GET /api/admin/dats</c>, Admin only (spec §23, §10.1).</summary>
public static class DatEndpoints
{
    public static RouteGroupBuilder MapDatEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/jobs/dat-refresh", async (HttpContext ctx, JobRunner runner, IServiceProvider services) =>
        {
            var job = new DatRefreshJob(
                services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClients.Dats),
                services.GetRequiredService<IOptions<SourcesOptions>>().Value,
                services.GetRequiredService<IOptions<PathsOptions>>().Value,
                services.GetRequiredService<DatCache>(),
                services.GetRequiredService<ISystemRegistry>(),
                services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<ILogger<DatRefreshJob>>());
            var started = await runner.StartAsync(job, JobTrigger.Manual, UserId(ctx.User), ctx.RequestAborted);
            return started is null ? JobRunning(ctx) : Results.Accepted(value: new { jobId = started.Id });
        }).RequireAuthorization(AuthSetup.AdminOnly);

        api.MapGet("/admin/dats", (DatCache cache) =>
        {
            IReadOnlyList<DatManifestEntry> manifest;
            try
            {
                manifest = DatManifest.Read(Path.Combine(cache.Directory, DatManifest.FileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                manifest = []; // no DATs yet; the page offers Refresh
            }
            // One row per system: metadat files share the DAT name and have no version.
            return Results.Ok(manifest
                .Where(e => e.File.StartsWith(DatKind.NoIntro + "/", StringComparison.Ordinal))
                .OrderBy(e => e.DatName, StringComparer.Ordinal)
                .Select(e => new { datName = e.DatName, version = e.Version, downloadedAt = e.DownloadedAt }));
        }).RequireAuthorization(AuthSetup.AdminOnly);

        return api;
    }

    public static IResult JobRunning(HttpContext ctx) =>
        Problems.Create(ctx, StatusCodes.Status409Conflict, "JobRunning", "Another job is running");

    public static int? UserId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
