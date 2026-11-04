using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using RnRomManager.Api.Auth;
using RnRomManager.Api.Configuration;
using RnRomManager.Api.Endpoints;
using RnRomManager.Api.Logging;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
using RnRomManager.Core.Users;
using RnRomManager.Data;
using RnRomManager.Data.Repositories;

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
        builder.Services.AddSingleton<IUserRepository, UserRepository>();
        builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        builder.Services.AddSingleton<AdminSeeder>();
        builder.Services.AddAppAuth();

        // Keys live in the DB so cookies survive restarts (spec §9.2).
        builder.Services.AddSingleton<IXmlRepository, DbXmlRepository>();
        builder.Services.AddDataProtection().SetApplicationName("RnRomManager");
        builder.Services.AddOptions<KeyManagementOptions>()
            .Configure<IXmlRepository>((options, repository) => options.XmlRepository = repository);

        builder.Services.AddHostedService<StartupRunner>();
        var app = builder.Build();

        var staticFiles = new StaticFileOptions { OnPrepareResponse = ctx => SetCacheHeaders(ctx) };
        app.UseStaticFiles(staticFiles);

        app.UseAppAuth();

        app.MapHealthEndpoints();
        // Deny by default: every /api endpoint needs a session unless it opts out (spec �23).
        app.MapGroup("/api")
            .RequireAuthorization(AuthSetup.Authenticated)
            .RequireAntiforgery()
            .MapAuthEndpoints();
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
