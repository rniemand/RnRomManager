using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;

namespace RnRomManager.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Neither returns details (spec §17.1).
        app.MapGet("/health/live", () => Results.Ok());
        app.MapGet("/health/ready", async (IDatabaseLifecycle database, IOptions<PathsOptions> paths, CancellationToken ct) =>
            paths.Value.All().All(p => Directory.Exists(p.Path)) && await database.PingAsync(ct)
                ? Results.Ok()
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        return app;
    }
}
