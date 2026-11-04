namespace RnRomManager.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Process is up. No body details (spec §17.1).
        app.MapGet("/health/live", () => Results.Ok());
        return app;
    }
}
