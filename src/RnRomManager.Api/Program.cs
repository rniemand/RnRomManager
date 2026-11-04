using NLog;
using RnRomManager.Api;
using RnRomManager.Api.Cli;

if (await CliDispatcher.TryRunAsync(args) is int exitCode)
    return exitCode;

try
{
    var app = AppHost.Build(args);
    await app.RunAsync();
    LogManager.Shutdown();
    return 0;
}
catch (HostAbortedException)
{
    // WebApplicationFactory stops Main after Build on purpose; its host keeps using NLog.
    throw;
}
catch (Exception ex)
{
    // Config validation and every startup step land here (spec §4.1): Fatal, flush, exit 1.
    LogManager.GetLogger("RnRomManager.Api.Program").Fatal(ex, "Startup failed: {Message}", ex.Message);
    LogManager.Shutdown();
    return 1;
}

public partial class Program;
