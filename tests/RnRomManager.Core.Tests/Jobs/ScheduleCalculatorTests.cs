using RnRomManager.Core.Jobs;

namespace RnRomManager.Core.Tests.Jobs;

/// <summary>Spec §4 <c>Integrity:QuickCheckTime</c> next-run rules incl. DST, and the §13 00:05 UTC log purge (S37).</summary>
public class ScheduleCalculatorTests
{
    /// <summary>A fixed CET/CEST-like zone (UTC+1, +1 h from the last Sunday of March 02:00 to the last Sunday of October 03:00), independent of OS tz data.</summary>
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test/CET", TimeSpan.FromHours(1), "Test CET", "Test CET",
        "Test CEST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Test]
    public void LaterToday_ElseTomorrow()
    {
        var three = new TimeOnly(3, 0);

        // 2026-01-10 01:00 UTC = 02:00 local → 03:00 local today = 02:00 UTC.
        Assert.That(ScheduleCalculator.NextDaily(Utc(1, 10, 1), three, Zone), Is.EqualTo(Utc(1, 10, 2)));
        // Exactly at the run time → tomorrow.
        Assert.That(ScheduleCalculator.NextDaily(Utc(1, 10, 2), three, Zone), Is.EqualTo(Utc(1, 11, 2)));
        // Late evening local, already the next UTC day boundary-wise.
        Assert.That(ScheduleCalculator.NextDaily(Utc(1, 10, 23, 30), three, Zone), Is.EqualTo(Utc(1, 11, 2)));
    }

    [Test]
    public void SummerTime_UsesTheDaylightOffset()
    {
        // 2026-07-01: UTC+2, so 03:00 local = 01:00 UTC.
        Assert.That(ScheduleCalculator.NextDaily(Utc(7, 1, 0), new TimeOnly(3, 0), Zone), Is.EqualTo(Utc(7, 1, 1)));
    }

    [Test]
    public void SkippedLocalTime_RunsAtTheFirstValidMinuteAfterIt()
    {
        // 2026-03-29: 02:00 → 03:00 local. 02:30 doesn't exist; the first valid minute after it is 03:00 CEST = 01:00 UTC.
        var next = ScheduleCalculator.NextDaily(Utc(3, 28, 12), new TimeOnly(2, 30), Zone);

        Assert.That(next, Is.EqualTo(Utc(3, 29, 1)));
        Assert.That(ScheduleCalculator.NextDaily(next, new TimeOnly(2, 30), Zone), Is.EqualTo(Utc(3, 30, 0, 30)), "back to 02:30 CEST the next day");
    }

    [Test]
    public void RepeatedLocalTime_RunsOnce_AtTheFirstOccurrence()
    {
        // 2026-10-25: 03:00 → 02:00 local. 02:30 happens at 00:30 UTC (CEST) and again at 01:30 UTC (CET).
        var first = ScheduleCalculator.NextDaily(Utc(10, 24, 12), new TimeOnly(2, 30), Zone);

        Assert.That(first, Is.EqualTo(Utc(10, 25, 0, 30)));
        Assert.That(ScheduleCalculator.NextDaily(first, new TimeOnly(2, 30), Zone), Is.EqualTo(Utc(10, 26, 1, 30)), "not again at 01:30 UTC");
    }

    [Test]
    public void LogPurge_IsDailyAt0005Utc()
    {
        Assert.That(ScheduleCalculator.NextLogPurge(Utc(5, 1, 0, 4)), Is.EqualTo(Utc(5, 1, 0, 5)));
        Assert.That(ScheduleCalculator.NextLogPurge(Utc(5, 1, 0, 5)), Is.EqualTo(Utc(5, 2, 0, 5)));
        Assert.That(ScheduleCalculator.NextLogPurge(Utc(12, 31, 23, 59)), Is.EqualTo(new DateTimeOffset(2027, 1, 1, 0, 5, 0, TimeSpan.Zero)));
    }
}
