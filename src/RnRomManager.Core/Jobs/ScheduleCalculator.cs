namespace RnRomManager.Core.Jobs;

/// <summary>Next-run times for the scheduler (spec §4 <c>Integrity:QuickCheckTime</c>, §13 log purge). All results are UTC.</summary>
public static class ScheduleCalculator
{
    /// <summary>The daily log purge (§13).</summary>
    public static readonly TimeOnly LogPurgeTimeUtc = new(0, 5);

    /// <summary>
    /// The first daily run at local <paramref name="time"/> strictly after <paramref name="nowUtc"/>. DST (§4): a skipped
    /// local time runs at the first valid minute after it; a repeated one runs once, at its first occurrence.
    /// </summary>
    public static DateTimeOffset NextDaily(DateTimeOffset nowUtc, TimeOnly time, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        for (var day = today.AddDays(-1); ; day = day.AddDays(1))
        {
            var local = day.ToDateTime(time, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(local))
                local = local.AddMinutes(1);
            // The first occurrence of a repeated time has the larger (daylight) offset.
            var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
            var run = new DateTimeOffset(local, offset).ToUniversalTime();
            if (run > nowUtc)
                return run;
        }
    }

    /// <summary>The next 00:05 UTC strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset NextLogPurge(DateTimeOffset nowUtc) => NextDaily(nowUtc, LogPurgeTimeUtc, TimeZoneInfo.Utc);
}
