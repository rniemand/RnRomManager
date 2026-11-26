using Microsoft.Extensions.Logging;
using RnRomManager.Core.Backups;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;

namespace RnRomManager.Core.Dats;

/// <summary>B28: loads and saves the DAT refresh schedule and tells the scheduler when it changes (as <see cref="BackupService"/> does for backups).</summary>
public sealed class DatRefreshSettingsService(
    ISettingsRepository settings,
    TimeZoneInfo zone,
    TimeProvider time,
    ILogger<DatRefreshSettingsService> logger)
{
    /// <summary>The container's time zone (<c>TZ</c>) the daily time is in.</summary>
    public string TimeZoneId => zone.Id;

    /// <summary>Raised after a save or <see cref="NotifySettingsChanged"/>, so the scheduler reschedules at once.</summary>
    public event Action? SettingsChanged;

    public async Task<DatRefreshSettings> GetAsync(CancellationToken cancellationToken) =>
        DatRefreshSettings.FromStored(await settings.GetAsync(DatRefreshSettings.Keys.ToList(), cancellationToken));

    /// <exception cref="ArgumentException">The time isn't valid (callers validate first).</exception>
    public async Task SaveAsync(DatRefreshSettings value, CancellationToken cancellationToken)
    {
        if (BackupSettings.TimeError(value.Time) is not null)
            throw new ArgumentException("Invalid DAT refresh settings.", nameof(value));
        await settings.SetAsync(value.ToStored(), cancellationToken);
        logger.LogInformation("DAT refresh settings saved: enabled {Enabled}, daily at {Time} ({Zone})", value.Enabled, value.Time, zone.Id);
        NotifySettingsChanged();
    }

    /// <summary>B27.6: the stored settings may have changed without a save (a backup restore); wakes the scheduler.</summary>
    public void NotifySettingsChanged() => SettingsChanged?.Invoke();

    /// <summary>The next scheduled run (UTC), or null when disabled.</summary>
    public DateTimeOffset? NextRunAt(DatRefreshSettings value) =>
        value.Enabled ? ScheduleCalculator.NextDaily(time.GetUtcNow(), value.TimeOfDay, zone) : null;
}
