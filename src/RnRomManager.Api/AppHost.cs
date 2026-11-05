using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using RnRomManager.Api.Auth;
using RnRomManager.Api.Configuration;
using RnRomManager.Api.Endpoints;
using RnRomManager.Api.Http;
using RnRomManager.Api.Logging;
using RnRomManager.Api.Security;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Database;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.Core.Users;
using RnRomManager.Data;
using RnRomManager.Data.Repositories;
using RnRomManager.Systems.GameBoy;

namespace RnRomManager.Api;

/// <summary>Builds the web host: endpoints, static UI and SPA fallback.</summary>
public static class AppHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = CreateBuilder(args);
        // First of every hosted service, the framework's included: Data Protection's warms its DB-backed key ring on
        // start, and nothing may touch the DB before §4.1 steps 5–6 (wait + migrate).
        builder.Services.Insert(0, ServiceDescriptor.Singleton<IHostedService, StartupRunner>());
        // Registered after StartupRunner, so it stops first (hosted services stop in reverse order).
        builder.Services.AddHostedService<JobShutdownService>();
        // Startup step 10: after StartupRunner (hosted services start in order); stops first.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scheduler>());
        var app = builder.Build();
        app.UseSecurityHeaders();

        var staticFiles = new StaticFileOptions { OnPrepareResponse = ctx => SetCacheHeaders(ctx) };
        app.UseStaticFiles(staticFiles);

        app.UseAppAuth();

        app.MapHealthEndpoints();
        // Deny by default: every /api endpoint needs a session unless it opts out (spec §23).
        app.MapGroup("/api")
            .RequireAuthorization(AuthSetup.Authenticated)
            .RequireAntiforgery()
            .MapAuthEndpoints()
            .MapUserEndpoints()
            .MapJobEndpoints()
            .MapDatEndpoints()
            .MapIngestEndpoints()
            .MapReviewEndpoints()
            .MapLibraryEndpoints()
            .MapIntegrityEndpoints()
            .MapAdminEndpoints();
        app.MapFallbackEndpoints(staticFiles);
        return app;
    }

    /// <summary>Config, NLog (startup step 2) and every service; shared with the CLI modes that need the DB.</summary>
    public static WebApplicationBuilder CreateBuilder(string[] args)
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
        builder.Services.AddSingleton<IJobRepository, JobRepository>();
        builder.Services.AddSingleton<IJobItemRepository, JobItemRepository>();
        builder.Services.AddSingleton<JobLock>();
        builder.Services.AddSingleton<JobRunner>();
        builder.Services.AddSingleton<CrashRecovery>();
        builder.Services.AddSingleton<IGameRepository, GameRepository>();
        builder.Services.AddSingleton<IRomRepository, RomRepository>();
        builder.Services.AddSingleton<IImageRepository, ImageRepository>();
        builder.Services.AddSingleton<ILibraryMirror, LibraryMirror>();
        builder.Services.AddSingleton<InfoJsonReader>();
        builder.Services.AddSingleton<DbSync>();
        builder.Services.AddSingleton<LibraryFiles>();
        // Transient: MediaService is a typed HttpClient (move to a new game fetches media).
        builder.Services.AddTransient<LibraryEditor>();
        builder.Services.AddTransient<GameMediaRetry>();
        builder.Services.AddTransient<MediaRetryJob>();
        builder.Services.AddSingleton<IntegrityJobFactory>(sp => mode => ActivatorUtilities.CreateInstance<IntegrityJob>(sp, mode));
        builder.Services.AddSingleton<LogRetention>();
        builder.Services.AddSingleton<LogArchive>();
        // The container's local time (TZ) drives Integrity:QuickCheckTime (§4).
        builder.Services.AddSingleton(TimeZoneInfo.Local);
        builder.Services.AddSingleton<Scheduler>();
        builder.Services.AddSingleton<DatCache>();
        builder.Services.AddSingleton<IIngestReviewRepository, IngestReviewRepository>();
        builder.Services.AddSingleton<IngestFileScanner>();
        // One per start: a job instance holds per-run state (and, later, the per-job circuit breaker).
        builder.Services.AddTransient<IngestJob>();
        builder.Services.AddTransient<IngestPipeline>();
        builder.Services.AddTransient<ReviewResolver>();
        builder.Services.AddSingleton<ArchiveExtractor>();
        builder.Services.AddSingleton<RnRomManager.Core.Integrity.IIntegrityIssueRepository, IntegrityIssueRepository>();
        builder.Services.AddExternalHttpClients();
        // §3.1: a running job gets 30 s to stop on SIGTERM (compose sets stop_grace_period: 35s).
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddAppAuth();
        builder.Services.AddSystemHandler<GameBoyHandler>();
        builder.Services.AddSystemHandler<GameBoyColorHandler>();

        // Keys live in the DB so cookies survive restarts (spec §9.2).
        builder.Services.AddSingleton<IXmlRepository, DbXmlRepository>();
        builder.Services.AddDataProtection().SetApplicationName("RnRomManager");
        builder.Services.AddOptions<KeyManagementOptions>()
            .Configure<IXmlRepository>((options, repository) => options.XmlRepository = repository);

        return builder;
    }

    // spec §11: hashed SvelteKit assets are immutable; everything else (index.html, theme.js) must revalidate.
    private static void SetCacheHeaders(StaticFileResponseContext ctx) =>
        ctx.Context.Response.Headers.CacheControl =
            ctx.Context.Request.Path.StartsWithSegments("/_app/immutable")
                ? "public, max-age=31536000, immutable"
                : "no-cache";
}
