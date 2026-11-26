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
            return await JobStart.StartManualAsync(runner, jobs(), ctx, ctx.RequestAborted);
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
            Results.Ok(ListedDats(cache, paths.Value.Cache)
                .OrderBy(e => e.DatName, StringComparer.Ordinal)
                .Select(e => new { datName = e.DatName, version = e.Version, downloadedAt = e.DownloadedAt })))
            .RequireAuthorization(AuthSetup.AdminOnly);

        // B29: one listed DAT as an attachment. The name is only a lookup key into the checked manifest; no client path is used.
        api.MapGet("/admin/dats/{datName}/download", (string datName, HttpContext ctx, DatCache cache, IOptions<PathsOptions> paths) =>
        {
            if (ListedDats(cache, paths.Value.Cache).FirstOrDefault(e => e.DatName == datName) is not { } entry)
                return Problems.NotFound(ctx);
            ExternalOpenResult opened;
            try
            {
                // Delete sharing, as DatManifest reads: a refresh may still swap cache/dats mid-download.
                opened = ExternalRomPath.OpenRead(paths.Value.Cache, Path.Combine(cache.Directory, entry.File), FileShare.Read | FileShare.Delete);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Problems.NotFound(ctx);
            }
            if (opened.Status != ExternalPathStatus.Found)
            {
                opened.Stream?.Dispose();
                return Problems.NotFound(ctx);
            }
            return Results.Stream(opened.Stream!, "application/octet-stream", Path.GetFileName(entry.File));
        }).RequireAuthorization(AuthSetup.AdminOnly).WithMetadata(MaintenanceExempt.Instance);

        return api;
    }

    /// <summary>
    /// The live manifest's listed entries, in manifest order: one per system, a no-intro or redump DAT (its metadat files share
    /// the name and have no version), a titledb file or a MAME file (Arcade D4). T3: only the live set, and only while it is a real directory below the
    /// cache root; the manifest is read through the same checked, capped reader as startup. Never cache/dats.previous/, even mid-swap.
    /// </summary>
    private static IEnumerable<DatManifestEntry> ListedDats(DatCache cache, string cacheRoot)
    {
        IReadOnlyList<DatManifestEntry> manifest = [];
        try
        {
            if (ExternalRomPath.ClassifyDirectory(cacheRoot, cache.Directory).Status == ExternalPathStatus.Found)
                manifest = DatManifest.ReadChecked(cacheRoot, Path.Combine(cache.Directory, DatManifest.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            manifest = []; // no usable DATs; the page offers Refresh
        }
        return manifest.Where(e => DatKind.IsMainDat(e.File) || e.File.StartsWith(TitleDb.Folder + "/", StringComparison.Ordinal)
            || e.File.StartsWith(MameDat.Folder + "/", StringComparison.Ordinal));
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

}
