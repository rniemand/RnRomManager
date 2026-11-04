namespace RnRomManager.Api.Endpoints;

public static class FallbackEndpoints
{
    /// <summary>Unknown <c>/api/*</c> → 404 ProblemDetails, unknown <c>/health/*</c> → 404, anything else → the SPA (spec §23).</summary>
    public static IEndpointRouteBuilder MapFallbackEndpoints(this IEndpointRouteBuilder app, StaticFileOptions staticFiles)
    {
        app.Map("/api/{**rest}", (HttpContext ctx) => Problems.NotFound(ctx));
        app.Map("/health/{**rest}", () => Results.NotFound());
        app.MapFallbackToFile("index.html", staticFiles);
        return app;
    }
}
