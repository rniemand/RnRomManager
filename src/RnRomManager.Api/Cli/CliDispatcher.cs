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

        var reset = Array.IndexOf(args, "--reset-password");
        if (reset >= 0)
        {
            if (reset + 1 >= args.Length || args[reset + 1].StartsWith('-'))
            {
                await Console.Error.WriteLineAsync("Usage: --reset-password <username>");
                return 1;
            }
            return await ResetPasswordCommand.RunAsync(args, args[reset + 1], Console.Out, Console.Error);
        }

        return null;
    }
}
