using Microsoft.AspNetCore.StaticFiles;
using RnRomManager.Api.Endpoints;

namespace RnRomManager.Api;

/// <summary>Builds the web host: endpoints, static UI and SPA fallback.</summary>
public static class AppHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        var staticFiles = new StaticFileOptions { OnPrepareResponse = ctx => SetCacheHeaders(ctx) };
        app.UseStaticFiles(staticFiles);

        app.MapHealthEndpoints();
        app.MapFallbackEndpoints(staticFiles);
        return app;
    }

    // spec §11: hashed SvelteKit assets are immutable; everything else (index.html, theme.js) must revalidate.
    private static void SetCacheHeaders(StaticFileResponseContext ctx) =>
        ctx.Context.Response.Headers.CacheControl =
            ctx.Context.Request.Path.StartsWithSegments("/_app/immutable")
                ? "public, max-age=31536000, immutable"
                : "no-cache";
}
