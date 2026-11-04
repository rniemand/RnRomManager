namespace RnRomManager.Api.Cli;

/// <summary>CLI modes that run instead of the web host (spec §17.1).</summary>
public static class CliDispatcher
{
    /// <returns>The exit code of a CLI mode, or null when the web host should start.</returns>
    public static async Task<int?> TryRunAsync(string[] args)
    {
        // Handled before anything else: no config validation, logging or DB.
        if (args.Contains("--healthcheck"))
            return await HealthCheckCommand.RunAsync(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));

        return null;
    }
}
