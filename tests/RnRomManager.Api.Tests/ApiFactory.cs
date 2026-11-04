using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NSubstitute;
using RnRomManager.Core.Database;

namespace RnRomManager.Api.Tests;

/// <summary>Test host with valid config pointing every path into its own temp folder (deleted on dispose).</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string Root { get; } = Directory.CreateTempSubdirectory("rnrm-api-").FullName;

    public Dictionary<string, string?> Settings { get; }

    public ApiFactory()
    {
        Settings = new()
        {
            ["ConnectionStrings:Default"] = "Server=localhost;Database=rnrommanager_test;User ID=test;Password=hunter2",
            ["Paths:Roms"] = Path.Combine(Root, "roms"),
            ["Paths:Ingest"] = Path.Combine(Root, "ingest"),
            ["Paths:Logs"] = Path.Combine(Root, "logs"),
            ["Paths:Cache"] = Path.Combine(Root, "cache"),
        };
        Database.PingAsync(Arg.Any<CancellationToken>()).Returns(true);
    }

    /// <summary>Stands in for MariaDB: wait/migrate succeed, ping is healthy unless a test says otherwise.</summary>
    public IDatabaseLifecycle Database { get; } = Substitute.For<IDatabaseLifecycle>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: that overlays the project's own wwwroot via static web assets.
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services => services.AddSingleton(Database));
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
