using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RnRomManager.Api.Configuration;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
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
    AdminSeeder adminSeeder) : IHostedService
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
        StorageFolders.EnsureSystemFolders(paths.Value, systems.All);

        // Steps 5–6
        await database.WaitUntilAvailableAsync(cancellationToken);
        database.Migrate();

        // Step 7 (crash recovery) comes with the job runner.
        // Step 8
        await adminSeeder.SeedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
