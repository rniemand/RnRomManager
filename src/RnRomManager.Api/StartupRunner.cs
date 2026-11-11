using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RnRomManager.Api.Configuration;
using RnRomManager.Core.Backups;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Database;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Users;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api;

/// <summary>
/// Owns the spec §4.1 startup sequence. Step 1 (config validation) runs just before this via ValidateOnStart,
/// step 2 (NLog) in <see cref="AppHost"/>. Hosted services start before Kestrel listens (step 11).
/// Any exception stops the host; Program logs it as Fatal and exits 1.
/// </summary>
public sealed class StartupRunner(
    ILogger<StartupRunner> logger,
    IServiceProvider services,
    IOptions<PathsOptions> paths,
    IDatabaseLifecycle database,
    ISystemRegistry systems,
    CrashRecovery crashRecovery,
    AdminSeeder adminSeeder,
    DatCache datCache,
    BackupRestorer backupRestorer) : IHostedService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Version { get; } =
        typeof(StartupRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0-dev";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Step 3
        logger.LogInformation("RnRomManager {Version} starting", Version);
        logger.LogInformation("Effective config: {Config}", JsonSerializer.Serialize(EffectiveSettings.From(services), Json));

        // Step 4
        StorageFolders.EnsureRoots(paths.Value);
        StorageFolders.EnsureSystemFolders(paths.Value, systems.All, logger);

        // Steps 5–6
        await database.WaitUntilAvailableAsync(cancellationToken);
        // B19: between 5 and 6, so migrations then apply anything newer than the backup.
        await backupRestorer.RestoreIfConfiguredAsync(cancellationToken);
        database.Migrate();

        // Step 7
        await crashRecovery.RunAsync(cancellationToken);

        // Step 8
        await adminSeeder.SeedAsync(cancellationToken);

        // Step 9: missing/corrupt DATs are logged at Error; that system just has none. An interrupted refresh's
        // backup is reconciled first (T2); an unsafe live set loads nothing.
        if (DatPublication.RecoverAtStartup(paths.Value.Cache, logger))
            datCache.Load();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
