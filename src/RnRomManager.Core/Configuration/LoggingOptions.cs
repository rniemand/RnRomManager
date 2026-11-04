namespace RnRomManager.Core.Configuration;

/// <summary>App keys in the <c>Logging</c> section (spec §4, §13).</summary>
public sealed class LoggingOptions
{
    public const string Section = "Logging";

    /// <summary>NLog level name for <c>RnRomManager.*</c> loggers.</summary>
    public string MinLevel { get; set; } = "Debug";

    public int RetentionDays { get; set; } = 14;
}
