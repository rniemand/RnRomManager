using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RnRomManager.Api.Auth;
using RnRomManager.Api.Http;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>POST /api/jobs/dat-refresh</c> and <c>GET /api/admin/dats</c>, Admin only (spec §23, §10.1).</summary>
public static class DatEndpoints
{
    public static RouteGroupBuilder MapDatEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/jobs/dat-refresh", async (HttpContext ctx, JobRunner runner, DatRefreshJobFactory jobs) =>
        {
            var started = await runner.StartAsync(jobs(), JobTrigger.Manual, UserId(ctx.User), ctx.RequestAborted);
            return started is null ? JobRunning(ctx) : Results.Accepted(value: new { jobId = started.Id });
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // B28: the daily refresh schedule; not audited, like the backup settings.
        api.MapGet("/admin/dats/settings", async (HttpContext ctx, DatRefreshSettingsService settings) =>
            Results.Ok(await SettingsAsync(settings, ctx.RequestAborted))).RequireAuthorization(AuthSetup.AdminOnly);

        api.MapPut("/admin/dats/settings", async (JsonElement body, HttpContext ctx, DatRefreshSettingsService settings) =>
        {
            var (value, errors) = ParseSettings(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            await settings.SaveAsync(value!, ctx.RequestAborted);
            return Results.Ok(await SettingsAsync(settings, ctx.RequestAborted));
        }).RequireAuthorization(AuthSetup.AdminOnly);

        api.MapGet("/admin/dats", (DatCache cache, IOptions<PathsOptions> paths) =>
        {
            // T3: only the live set, and only while it is a real directory below the cache root; the manifest is read
            // through the same checked, capped reader as startup. Never cache/dats.previous/, even mid-swap.
            IReadOnlyList<DatManifestEntry> manifest = [];
            try
            {
                if (ExternalRomPath.ClassifyDirectory(paths.Value.Cache, cache.Directory).Status == ExternalPathStatus.Found)
                    manifest = DatManifest.ReadChecked(paths.Value.Cache, Path.Combine(cache.Directory, DatManifest.FileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                manifest = []; // no usable DATs; the page offers Refresh
            }
            // One row per system: a no-intro or redump DAT (its metadat files share the name and have no version) or a titledb file.
            return Results.Ok(manifest
                .Where(e => DatKind.IsMainDat(e.File) || e.File.StartsWith(TitleDb.Folder + "/", StringComparison.Ordinal))
                .OrderBy(e => e.DatName, StringComparer.Ordinal)
                .Select(e => new { datName = e.DatName, version = e.Version, downloadedAt = e.DownloadedAt }));
        }).RequireAuthorization(AuthSetup.AdminOnly);

        return api;
    }

    private static async Task<object> SettingsAsync(DatRefreshSettingsService settings, CancellationToken ct)
    {
        var value = await settings.GetAsync(ct);
        return new { value.Enabled, value.Time, TimeZone = settings.TimeZoneId, NextRunAt = settings.NextRunAt(value) };
    }

    /// <summary><c>{enabled: bool, time: "HH:mm"}</c>, both required (B28), parsed like the backup settings.</summary>
    public static (DatRefreshSettings? Settings, Dictionary<string, string[]> Errors) ParseSettings(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors[""] = ["A JSON object is required."];
            return (null, errors);
        }

        bool? enabled = body.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean()
            : null;
        if (enabled is null)
            errors["enabled"] = ["Must be true or false."];

        string? time = body.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (RnRomManager.Core.Backups.BackupSettings.TimeError(time) is { } timeError)
            errors["time"] = [timeError];

        return errors.Count > 0 ? (null, errors) : (new DatRefreshSettings(enabled!.Value, time!), errors);
    }

    public static IResult JobRunning(HttpContext ctx) =>
        Problems.Create(ctx, StatusCodes.Status409Conflict, "JobRunning", "Another job is running");

    public static int? UserId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
