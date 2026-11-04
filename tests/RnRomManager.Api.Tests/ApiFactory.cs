using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NLog;

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
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: that overlays the project's own wwwroot via static web assets.
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);
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
