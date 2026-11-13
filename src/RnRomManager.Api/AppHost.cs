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
using RnRomManager.Core.Audit;
using RnRomManager.Core.Backups;
using RnRomManager.Core.Collections;
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
using RnRomManager.Systems.Nes;
using RnRomManager.Systems.Sony;
using RnRomManager.Systems.NintendoDisc;
using RnRomManager.Systems.Microsoft;
using RnRomManager.Systems.Atari;
using RnRomManager.Systems.Coleco;
using RnRomManager.Systems.Sega;
using RnRomManager.Systems.Nec;
using RnRomManager.Systems.Switch;
using RnRomManager.Systems.Snes;
using RnRomManager.Systems.Nintendo64;

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
        // AU2: before JobShutdownService, so it stops after it and drains what the cancelled job recorded.
        builder.Services.AddSingleton<AuditWriter>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditWriter>());
        // Registered after StartupRunner, so it stops first (hosted services stop in reverse order).
        builder.Services.AddHostedService<JobShutdownService>();
        // Startup step 10: after StartupRunner (hosted services start in order); stops first.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scheduler>());
        // B16: after StartupRunner (the restore and migrations run first).
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupScheduler>());
        var app = builder.Build();
        app.UseSecurityHeaders();

        // Browser emulators B10: EmulatorJS cores are .data files, which the default table doesn't know (they'd 404).
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".data"] = "application/octet-stream";
        var staticFiles = new StaticFileOptions { ContentTypeProvider = contentTypes, OnPrepareResponse = ctx => SetCacheHeaders(ctx) };
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
            .MapCollectionEndpoints()
            .MapIntegrityEndpoints()
            .MapReportEndpoints()
            .MapAuditEndpoints()
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
        // Only the image upload (I5) reads forms; match its request limit.
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(
            options => options.MultipartBodyLengthLimit = Endpoints.LibraryEndpoints.UploadRequestLimit);
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
        builder.Services.AddSingleton<IBackupStore, MySqlBackupStore>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<BackupRestorer>();
        builder.Services.AddSingleton<ISettingsRepository, SettingsRepository>();
        builder.Services.AddSingleton<BackupScheduler>();
        builder.Services.AddSingleton<IAuditRepository, AuditRepository>();
        builder.Services.AddSingleton<AuditLog>();
        builder.Services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<AuditLog>());
        builder.Services.AddSingleton<AuditPaths>();
        builder.Services.AddSingleton<ICollectionRepository, CollectionRepository>();
        builder.Services.AddSingleton<CollectionCovers>();
        builder.Services.AddSingleton<CollectionService>();
        builder.Services.AddSingleton<IGameRepository, GameRepository>();
        builder.Services.AddSingleton<IRomRepository, RomRepository>();
        builder.Services.AddSingleton<IImageRepository, ImageRepository>();
        builder.Services.AddSingleton<ILibraryMirror, LibraryMirror>();
        builder.Services.AddSingleton<IInfoJsonUpgrader, InfoJsonUpgraderV1>();
        builder.Services.AddSingleton<IInfoJsonUpgrader, InfoJsonUpgraderV2>();
        builder.Services.AddSingleton<IInfoJsonUpgrader, InfoJsonUpgraderV3>();
        builder.Services.AddSingleton<IInfoJsonUpgrader, InfoJsonUpgraderV4>();
        builder.Services.AddSingleton<IInfoJsonUpgrader, InfoJsonUpgraderV5>();
        builder.Services.AddSingleton<IInfoJsonUpgrader, InfoJsonUpgraderV6>();
        builder.Services.AddSingleton<InfoJsonReader>();
        builder.Services.AddSingleton<DbSync>();
        builder.Services.AddSingleton<LibraryFiles>();
        // Transient: MediaService is a typed HttpClient (move to a new game fetches media).
        builder.Services.AddTransient<LibraryEditor>();
        builder.Services.AddTransient<GameMediaRetry>();
        builder.Services.AddTransient<MediaRetryJob>();
        // The ZIP path is set afterwards: ActivatorUtilities can't match a null string? argument.
        builder.Services.AddSingleton<IntegrityJobFactory>(sp => (mode, zipPath) =>
        {
            var job = ActivatorUtilities.CreateInstance<IntegrityJob>(sp, mode);
            job.ZipPath = zipPath;
            return job;
        });
        builder.Services.AddSingleton<LogRetention>();
        builder.Services.AddSingleton<LogArchive>();
        // The container's local time (TZ) drives Integrity:QuickCheckTime (§4).
        builder.Services.AddSingleton(TimeZoneInfo.Local);
        builder.Services.AddSingleton<Scheduler>();
        builder.Services.AddSingleton<DatCache>();
        builder.Services.AddSingleton<IIngestReviewRepository, IngestReviewRepository>();
        builder.Services.AddSingleton<RnRomManager.Core.Reports.IReportRepository, ReportRepository>();
        builder.Services.AddSingleton<IngestFileScanner>();
        builder.Services.AddSingleton<IngestFileDeleter>();
        builder.Services.AddSingleton<RemovedFileRestorer>();
        // One per start: a job instance holds per-run state (and, later, the per-job circuit breaker).
        builder.Services.AddTransient<IngestJob>();
        builder.Services.AddTransient<IngestPipeline>();
        builder.Services.AddTransient<ReviewResolver>();
        builder.Services.AddTransient<ReviewResolveJob>();
        builder.Services.AddTransient<ReviewSets>();
        builder.Services.AddSingleton<ArchiveExtractor>();
        builder.Services.AddSingleton<RnRomManager.Core.Integrity.IIntegrityIssueRepository, IntegrityIssueRepository>();
        builder.Services.AddExternalHttpClients();
        // §3.1: a running job gets 30 s to stop on SIGTERM (compose sets stop_grace_period: 35s).
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddAppAuth();
        AddSystemHandlers(builder.Services);

        // Keys live in the DB so cookies survive restarts (spec §9.2).
        builder.Services.AddSingleton<IXmlRepository, DbXmlRepository>();
        builder.Services.AddDataProtection().SetApplicationName("RnRomManager");
        builder.Services.AddOptions<KeyManagementOptions>()
            .Configure<IXmlRepository>((options, repository) => options.XmlRepository = repository);

        return builder;
    }

    public static void AddSystemHandlers(IServiceCollection services)
    {
        services.AddSystemHandler<GameBoyHandler>();
        services.AddSystemHandler<GameBoyColorHandler>();
        services.AddSystemHandler<GameBoyAdvanceHandler>();
        services.AddSystemHandler<Nintendo64Handler>();
        services.AddSystemHandler<NesHandler>();
        services.AddSystemHandler<SnesHandler>();
        services.AddSystemHandler<Atari2600Handler>();
        services.AddSystemHandler<AtariLynxHandler>();
        services.AddSystemHandler<AtariJaguarHandler>();
        services.AddSystemHandler<ColecoVisionHandler>();
        services.AddSystemHandler<GameGearHandler>();
        services.AddSystemHandler<MasterSystemHandler>();
        services.AddSystemHandler<MegaDriveHandler>();
        services.AddSystemHandler<Sega32xHandler>();
        services.AddSystemHandler<TurboGrafx16Handler>();
        services.AddSystemHandler<SwitchHandler>();
        services.AddSystemHandler<PlayStationHandler>();
        services.AddSystemHandler<PlayStationPortableHandler>();
        services.AddSystemHandler<GameCubeHandler>();
        services.AddSystemHandler<WiiHandler>();
        services.AddSystemHandler<Xbox360Handler>();
        services.AddSystemHandler<SaturnHandler>();
    }

    // spec §11: hashed SvelteKit assets are immutable; everything else (index.html, theme.js) must revalidate.
    private static void SetCacheHeaders(StaticFileResponseContext ctx) =>
        ctx.Context.Response.Headers.CacheControl =
            ctx.Context.Request.Path.StartsWithSegments("/_app/immutable")
                ? "public, max-age=31536000, immutable"
                : "no-cache";
}
