namespace RnRomManager.Api.Endpoints;

public static class FallbackEndpoints
{
    /// <summary>
    /// Unknown <c>/api/*</c> → 404 ProblemDetails, unknown <c>/health/*</c> and extensionless <c>/emulator/*</c> → 404,
    /// anything else → the SPA (spec §23). <c>:nonfile</c> matters: a matched endpoint makes the static file middleware
    /// skip the request, so <c>/emulator/{**rest}</c> would 404 every vendored file (browser emulators B10).
    /// </summary>
    public static IEndpointRouteBuilder MapFallbackEndpoints(this IEndpointRouteBuilder app, StaticFileOptions staticFiles)
    {
        app.Map("/api/{**rest}", (HttpContext ctx) => Problems.NotFound(ctx));
        app.Map("/health/{**rest}", () => Results.NotFound());
        app.Map("/emulator/{**rest:nonfile}", () => Results.NotFound());
        app.MapFallbackToFile("index.html", staticFiles);
        return app;
    }
}
