using Microsoft.Extensions.Options;

namespace RnRomManager.Core.Configuration;

public sealed class LoggingOptionsValidator : IValidateOptions<LoggingOptions>
{
    // NLog level names (case-insensitive in NLog too).
    public static readonly IReadOnlyList<string> Levels = ["Trace", "Debug", "Info", "Warn", "Error", "Fatal", "Off"];

    public ValidateOptionsResult Validate(string? name, LoggingOptions options)
    {
        var failures = new List<string>();
        if (!Levels.Contains(options.MinLevel, StringComparer.OrdinalIgnoreCase))
            failures.Add($"Logging:MinLevel must be one of {string.Join(", ", Levels)} (got '{options.MinLevel}').");
        if (options.RetentionDays is < 1 or > 365)
            failures.Add($"Logging:RetentionDays must be 1-365 (got {options.RetentionDays}).");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
