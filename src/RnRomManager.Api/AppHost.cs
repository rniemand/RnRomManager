using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using RnRomManager.Api.Configuration;
using RnRomManager.Api.Endpoints;
using RnRomManager.Api.Logging;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
using RnRomManager.Data;

namespace RnRomManager.Api;

/// <summary>Builds the web host: endpoints, static UI and SPA fallback.</summary>
public static class AppHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        NLogSetup.Configure(builder);
        builder.Services.AddAppOptions(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        // Parsed when first resolved (startup step 5), after config validation.
        builder.Services.AddSingleton(sp =>
            new MySqlConnectionFactory(sp.GetRequiredService<IOptions<ConnectionStringsOptions>>().Value.Default));
        builder.Services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<MySqlConnectionFactory>());
        builder.Services.AddSingleton<IDatabaseLifecycle, MySqlDatabaseLifecycle>();
        builder.Services.AddHostedService<StartupRunner>();
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
