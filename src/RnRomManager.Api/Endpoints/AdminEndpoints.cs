using System.Text.Json;
using RnRomManager.Api.Auth;
using RnRomManager.Api.Configuration;
using RnRomManager.Api.Logging;
using RnRomManager.Core.Backups;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>GET /api/admin/settings</c>, <c>GET /api/admin/logs</c>, <c>GET /api/admin/logs/recent</c>, <c>GET /api/admin/logs/stream</c>, the backup endpoints (B17) (Admin) and <c>GET /api/about</c> (any user) — spec §23, §13, §10.4.</summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").RequireAuthorization(AuthSetup.AdminOnly);

        // The same redacted model the startup log line uses (§4.1 step 3).
        admin.MapGet("/settings", (IServiceProvider services) => Results.Ok(EffectiveSettings.From(services)));

        admin.MapGet("/logs", async (HttpContext ctx, LogArchive archive, TimeProvider time) =>
        {
            var zip = await archive.BuildAsync(ctx.RequestAborted);
            return Results.Stream(zip, "application/zip", $"rnrommanager-logs-{time.GetUtcNow():yyyyMMdd-HHmmss}.zip");
        });

        admin.MapGet("/logs/recent", (LogBuffer buffer) => Results.Ok(buffer.Recent(20)));

        admin.MapGet("/logs/stream", LogStream.RunAsync);

        // B17: database backups.
        admin.MapGet("/backups", async (BackupService backups, CancellationToken ct) =>
            Results.Ok(await BackupStatusAsync(backups, ct)));

        admin.MapPut("/backups/settings", async (JsonElement body, HttpContext ctx, BackupService backups) =>
        {
            var (settings, errors) = ParseBackupSettings(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            await backups.SaveSettingsAsync(settings!, ctx.RequestAborted);
            return Results.Ok(await BackupStatusAsync(backups, ctx.RequestAborted));
        });

        admin.MapPost("/backups", async (HttpContext ctx, BackupService backups) =>
        {
            var settings = await backups.GetSettingsAsync(ctx.RequestAborted);
            try
            {
                var file = await backups.CreateAsync(settings.Retention, ctx.RequestAborted);
                return Results.Json(file, statusCode: StatusCodes.Status201Created);
            }
            catch (BackupRunningException)
            {
                return Problems.Create(ctx, StatusCodes.Status409Conflict, "BackupRunning", "A backup is already running");
            }
            catch (BackupFailedException)
            {
                // The cause is logged at Error by BackupService; the client only learns that it failed.
                return Problems.Create(ctx, StatusCodes.Status500InternalServerError, "BackupFailed", "The backup failed");
            }
        });

        api.MapGet("/about", () => Results.Ok(new { version = StartupRunner.Version, licenses = LicenseInfo.All }));

        return api;
    }

    private static async Task<object> BackupStatusAsync(BackupService backups, CancellationToken ct)
    {
        var settings = await backups.GetSettingsAsync(ct);
        return new
        {
            settings.Enabled,
            settings.Time,
            settings.Retention,
            TimeZone = backups.TimeZoneId,
            NextRunAt = backups.NextRunAt(settings),
            Running = backups.IsRunning,
            LastResult = backups.LastResult,
            Backups = backups.List(),
        };
    }

    /// <summary><c>{enabled: bool, time: "HH:mm", retention: 1–365}</c>, all required (B15, B17).</summary>
    public static (BackupSettings? Settings, Dictionary<string, string[]> Errors) ParseBackupSettings(JsonElement body)
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
        if (BackupSettings.TimeError(time) is { } timeError)
            errors["time"] = [timeError];

        int? retention = body.TryGetProperty("retention", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var n)
            ? n
            : null;
        if (retention is null || BackupSettings.RetentionError(retention.Value) is not null)
            errors["retention"] = [BackupSettings.RetentionError(0)!];

        return errors.Count > 0 ? (null, errors) : (new BackupSettings(enabled!.Value, time!, retention!.Value), errors);
    }
}
