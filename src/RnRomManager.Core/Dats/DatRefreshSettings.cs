using System.Globalization;
using RnRomManager.Core.Backups;

namespace RnRomManager.Core.Dats;

/// <summary>B28: the daily DAT refresh, stored in <c>app_settings</c>; missing or invalid rows mean the defaults.</summary>
public sealed record DatRefreshSettings(bool Enabled, string Time)
{
    public const string EnabledKey = "dats.autoRefresh.enabled";
    public const string TimeKey = "dats.autoRefresh.time";

    public static DatRefreshSettings Defaults { get; } = new(true, "04:00");

    public static IReadOnlyList<string> Keys { get; } = [EnabledKey, TimeKey];

    /// <summary>The time as a <see cref="TimeOnly"/> (only valid settings exist).</summary>
    public TimeOnly TimeOfDay => TimeOnly.ParseExact(Time, "HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Stored rows over the defaults, as <see cref="BackupSettings.FromStored"/>.</summary>
    public static DatRefreshSettings FromStored(IReadOnlyDictionary<string, string> stored)
    {
        var settings = Defaults;
        if (stored.TryGetValue(EnabledKey, out var enabled) && bool.TryParse(enabled, out var e))
            settings = settings with { Enabled = e };
        if (stored.TryGetValue(TimeKey, out var time) && BackupSettings.TimeError(time) is null)
            settings = settings with { Time = time };
        return settings;
    }

    public IReadOnlyDictionary<string, string> ToStored() => new Dictionary<string, string>
    {
        [EnabledKey] = Enabled ? "true" : "false",
        [TimeKey] = Time,
    };
}
