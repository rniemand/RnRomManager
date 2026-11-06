using RnRomManager.Api.Auth;
using RnRomManager.Api.Configuration;
using RnRomManager.Api.Logging;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>GET /api/admin/settings</c>, <c>GET /api/admin/logs</c>, <c>GET /api/admin/logs/recent</c>, <c>GET /api/admin/logs/stream</c> (Admin) and <c>GET /api/about</c> (any user) — spec §23, §13, §10.4.</summary>
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

        api.MapGet("/about", () => Results.Ok(new { version = StartupRunner.Version, licenses = LicenseInfo.All }));

        return api;
    }
}
