using RnRomManager.Api;
using RnRomManager.Api.Cli;

if (await CliDispatcher.TryRunAsync(args) is int exitCode)
    return exitCode;

var app = AppHost.Build(args);
await app.RunAsync();
return 0;

public partial class Program;
