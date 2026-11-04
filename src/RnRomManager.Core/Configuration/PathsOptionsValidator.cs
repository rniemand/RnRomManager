using Microsoft.Extensions.Options;

namespace RnRomManager.Core.Configuration;

/// <summary>Required, absolute, distinct, none inside another (spec §4). Folder creation + write probe run later.</summary>
public sealed class PathsOptionsValidator : IValidateOptions<PathsOptions>
{
    public ValidateOptionsResult Validate(string? name, PathsOptions options)
    {
        var failures = new List<string>();
        var valid = new List<(string Key, string Full)>();

        foreach (var (key, path) in options.All())
        {
            if (string.IsNullOrWhiteSpace(path))
                failures.Add($"{key} is required.");
            else if (!Path.IsPathFullyQualified(path))
                failures.Add($"{key} must be an absolute path (got '{path}').");
            else
                valid.Add((key, WithTrailingSeparator(Path.GetFullPath(path))));
        }

        // Case-insensitive: file-name comparisons are case-insensitive everywhere (spec §5).
        for (var i = 0; i < valid.Count; i++)
        {
            for (var j = i + 1; j < valid.Count; j++)
            {
                var (a, b) = (valid[i], valid[j]);
                if (string.Equals(a.Full, b.Full, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{a.Key} and {b.Key} must be different paths.");
                else if (b.Full.StartsWith(a.Full, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{b.Key} must not be inside {a.Key}.");
                else if (a.Full.StartsWith(b.Full, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{a.Key} must not be inside {b.Key}.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // So /app/roms2 isn't treated as inside /app/roms.
    private static string WithTrailingSeparator(string path) =>
        Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
}
