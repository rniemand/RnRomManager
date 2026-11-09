using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Tests;

/// <summary>
/// Test host with valid config pointing every path into its own temp folder (deleted on dispose).
/// Without a connection string the database is stubbed (no MariaDB needed); with one, the real migrations,
/// admin seed and key storage run against it (Integration tests).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public ApiFactory(string? connectionString = null)
    {
        UsesRealDatabase = connectionString is not null;
        Settings = new()
        {
            ["ConnectionStrings:Default"] = connectionString ?? "Server=localhost;Database=rnrommanager_test;User ID=test;Password=hunter2",
            ["Paths:Roms"] = Path.Combine(Root, "roms"),
            ["Paths:Ingest"] = Path.Combine(Root, "ingest"),
            ["Paths:Logs"] = Path.Combine(Root, "logs"),
            ["Paths:Cache"] = Path.Combine(Root, "cache"),
            ["Paths:Backups"] = Path.Combine(Root, "backups"),
            ["Paths:Collections"] = Path.Combine(Root, "collections"),
            // No scheduled quick check unless a test turns it on (it would start a real job at 03:00).
            ["Integrity:QuickCheckTime"] = "",
        };
        Database.PingAsync(Arg.Any<CancellationToken>()).Returns(true);
        Users.CountAsync(Arg.Any<CancellationToken>()).Returns(1);
        AppSettings.GetAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["backup.enabled"] = "false" });
        Jobs.AbortRunningAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Guid>());
    }

    public string Root { get; } = Directory.CreateTempSubdirectory("rnrm-api-").FullName;

    public Dictionary<string, string?> Settings { get; }

    public bool UsesRealDatabase { get; }

    /// <summary>Replaces the app's clock when set (lockout tests).</summary>
    public TimeProvider? Time { get; init; }

    /// <summary>Extra service overrides (e.g. a stub HTTP handler).</summary>
    public Action<IServiceCollection>? TestServices { get; init; }

    /// <summary>Stub DB lifecycle (stub mode only): wait/migrate succeed, ping is healthy unless a test says otherwise.</summary>
    public IDatabaseLifecycle Database { get; } = Substitute.For<IDatabaseLifecycle>();

    /// <summary>Stub users table (stub mode only): reports one user, so startup doesn't seed.</summary>
    public IUserRepository Users { get; } = Substitute.For<IUserRepository>();

    /// <summary>Stub <c>app_settings</c> (stub mode only): scheduled backups are off, so nothing touches a database.</summary>
    public ISettingsRepository AppSettings { get; } = Substitute.For<ISettingsRepository>();

    /// <summary>Stub jobs table (stub mode only): nothing to abort at startup.</summary>
    public IJobRepository Jobs { get; } = Substitute.For<IJobRepository>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: that overlays the project's own wwwroot via static web assets.
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);

        if (Time is not null)
            builder.ConfigureTestServices(services => services.AddSingleton(Time));
        if (TestServices is not null)
            builder.ConfigureTestServices(TestServices);

        if (!UsesRealDatabase)
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(Database);
                services.AddSingleton(Users);
                services.AddSingleton(Jobs);
                services.AddSingleton(AppSettings);
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // WebApplicationFactory can call this twice.
        if (!disposing || !Directory.Exists(Root))
            return;
        // NLog keeps today's log file open; release it before deleting the folder.
        LogManager.Shutdown();
        Directory.Delete(Root, recursive: true);
    }
}
