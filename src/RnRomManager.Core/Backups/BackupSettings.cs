using System.Globalization;
using System.Text.RegularExpressions;

namespace RnRomManager.Core.Backups;

/// <summary>B15: the backup schedule, stored in <c>app_settings</c>; missing rows mean the defaults.</summary>
public sealed partial record BackupSettings(bool Enabled, string Time, int Retention)
{
    public const string EnabledKey = "backup.enabled";
    public const string TimeKey = "backup.time";
    public const string RetentionKey = "backup.retention";
    public const int MinRetention = 1;
    public const int MaxRetention = 365;

    public static BackupSettings Defaults { get; } = new(true, "03:30", 30);

    public static IReadOnlyList<string> Keys { get; } = [EnabledKey, TimeKey, RetentionKey];

    /// <summary>The time as a <see cref="TimeOnly"/> (only valid settings exist).</summary>
    public TimeOnly TimeOfDay => TimeOnly.ParseExact(Time, "HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Stored rows over the defaults; a row that isn't valid any more falls back to its default.</summary>
    public static BackupSettings FromStored(IReadOnlyDictionary<string, string> stored)
    {
        var settings = Defaults;
        if (stored.TryGetValue(EnabledKey, out var enabled) && bool.TryParse(enabled, out var e))
            settings = settings with { Enabled = e };
        if (stored.TryGetValue(TimeKey, out var time) && TimeError(time) is null)
            settings = settings with { Time = time };
        if (stored.TryGetValue(RetentionKey, out var retention)
            && int.TryParse(retention, NumberStyles.None, CultureInfo.InvariantCulture, out var r) && RetentionError(r) is null)
            settings = settings with { Retention = r };
        return settings;
    }

    public IReadOnlyDictionary<string, string> ToStored() => new Dictionary<string, string>
    {
        [EnabledKey] = Enabled ? "true" : "false",
        [TimeKey] = Time,
        [RetentionKey] = Retention.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Field errors keyed <c>time</c> / <c>retention</c>; empty when valid.</summary>
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (TimeError(Time) is { } time) errors["time"] = [time];
        if (RetentionError(Retention) is { } retention) errors["retention"] = [retention];
        return errors;
    }

    public static string? TimeError(string? time) =>
        time is not null && TimePattern().IsMatch(time) ? null : "Use HH:mm, 00:00–23:59.";

    public static string? RetentionError(int retention) =>
        retention is >= MinRetention and <= MaxRetention ? null : $"Keep {MinRetention}–{MaxRetention} backups.";

    // [0-9], not \d: \d also matches non-ASCII digits, which TimeOnly.ParseExact rejects.
    [GeneratedRegex(@"^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();
}
