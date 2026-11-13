using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NSubstitute;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Dats;
using RnRomManager.Api.Tests.Integration;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests;

/// <summary>
/// Test host with valid config pointing every path into its own temp folder (deleted on dispose).
/// Without a connection string the database is stubbed (no MariaDB needed); with one, the real migrations,
/// admin seed and key storage run against it (Integration tests).
/// <para>
/// A test class can share one host between its tests: create it in <c>[OneTimeSetUp]</c>, call
/// <see cref="StartSharedAsync"/>, and call <see cref="ResetAsync"/> at the start of every test. Starting a host costs
/// far more than most tests, so classes that don't change its configuration per test should share one.
/// </para>
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

    /// <summary>Stub audit table (stub mode only): the scheduler's startup purge deletes nothing.</summary>
    public IAuditRepository Audit { get; } = Substitute.For<IAuditRepository>();

    /// <summary>Header naming the simulated browser; it becomes the request's client IP (see <see cref="AuthClient"/>).</summary>
    public const string ClientHeader = "X-Test-Client";

    private HostFilesSnapshot? _folders;

    /// <summary>
    /// Starts the host once for a whole test class and records the state it started with (database rows and
    /// files), so <see cref="ResetAsync"/> can return to it before each test.
    /// </summary>
    public async Task StartSharedAsync()
    {
        if (!UsesRealDatabase)
            throw new InvalidOperationException("Shared hosts need a real database.");
        // Startup, then one request: the antiforgery key is created (and stored in the DB) before the snapshot.
        using (var client = CreateClient())
            (await client.GetAsync("/api/auth/antiforgery")).Dispose();
        await Services.GetRequiredService<AuditWriter>().FlushAsync();
        await MariaDbServer.SnapshotAsync(ConnectionString);
        // NLog keeps today's log file open; logs aren't test state.
        _folders = HostFilesSnapshot.Take(Root, "logs");
    }

    /// <summary>
    /// Puts a shared host back to its just-started state: waits for a running job and pending audit events, restores
    /// the database rows and the files under <see cref="Root"/>, and reloads the DATs from the restored cache.
    /// </summary>
    public async Task ResetAsync()
    {
        if (_folders is null)
            throw new InvalidOperationException("Call StartSharedAsync first.");
        await WaitForIdleJobAsync();
        await Services.GetRequiredService<AuditWriter>().FlushAsync();
        await MariaDbServer.RestoreAsync(ConnectionString);
        _folders.Restore();
        Services.GetRequiredService<DatCache>().Load();
    }

    private string ConnectionString => Settings["ConnectionStrings:Default"]!;

    private async Task WaitForIdleJobAsync()
    {
        var jobLock = Services.GetRequiredService<JobLock>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            using (var held = jobLock.TryAcquire())
            {
                if (held is not null)
                    return;
            }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The previous test left a job running.");
            await Task.Delay(20);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: that overlays the project's own wwwroot via static web assets.
        builder.UseEnvironment("Testing");
        // Each simulated browser gets its own client IP, so per-client limits (login) don't leak between tests.
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, ClientIpStartupFilter>();
            // Same algorithm, one PBKDF2 round: the default 100k cost ~50 ms per hash, several times per test.
            services.Configure<PasswordHasherOptions>(options => options.IterationCount = TestUsers.HashIterations);
        });
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
                services.AddSingleton(Audit);
                // Otherwise data protection reads its keys from MariaDB; with no server each host waits for the
                // connection to be refused (seconds per test on Windows).
                services.AddSingleton<IXmlRepository, InMemoryXmlRepository>();
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

    private sealed class ClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(ClientHeader, out var client) && int.TryParse(client, out var id))
                    context.Connection.RemoteIpAddress = IPAddress.Parse($"10.{(id >> 16) & 255}.{(id >> 8) & 255}.{id & 255}");
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>Stub-mode key ring: keys live as long as the host.</summary>
    private sealed class InMemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_elements)
                return _elements.Select(e => new XElement(e)).ToList();
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_elements)
                _elements.Add(new XElement(element));
        }
    }
}
