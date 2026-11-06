using NLog;
using NLog.Web;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Api.Logging;

/// <summary>Startup step 2 (spec §4.1, §13): load nlog.config, inject config values, route MEL through NLog.</summary>
public static class NLogSetup
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var paths = builder.Configuration.GetSection(PathsOptions.Section).Get<PathsOptions>() ?? new PathsOptions();
        var logging = builder.Configuration.GetSection(LoggingOptions.Section).Get<LoggingOptions>() ?? new LoggingOptions();

        var config = LogManager.Setup()
            .RegisterNLogWeb()
            .SetupExtensions(extensions => extensions.RegisterTarget<MemoryLogTarget>("LiveLog"))
            .LoadConfigurationFromFile(Path.Combine(AppContext.BaseDirectory, "nlog.config"))
            .LogFactory.Configuration!;

        // Config isn't validated yet (that's ValidateOnStart). Keep NLog working on bad values so the
        // validation failure itself still reaches the console as Fatal.
        if (Path.IsPathFullyQualified(paths.Logs))
            config.Variables["logDir"] = paths.Logs;
        else
            config.RemoveTarget("file");

        if (LoggingOptionsValidator.Levels.Contains(logging.MinLevel, StringComparer.OrdinalIgnoreCase))
            config.Variables["minLevel"] = logging.MinLevel;

        config.Variables["retentionDays"] = logging.RetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // One buffer per host; NLog's configuration is process-wide, so the newest host's buffer receives events.
        var buffer = new LogBuffer(TimeProvider.System);
        config.FindTargetByName<MemoryLogTarget>("live")!.Buffer = buffer;
        builder.Services.AddSingleton(buffer);
        builder.Services.AddSingleton(new LogStreamOptions());
        LogManager.ReconfigExistingLoggers();

        // MEL would otherwise drop Debug before NLog sees it; NLog rules do all filtering.
        builder.Logging.ClearProviders().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
        builder.Host.UseNLog();
    }
}
