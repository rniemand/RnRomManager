using Microsoft.Extensions.Time.Testing;

namespace RnRomManager.Api.Tests;

/// <summary>Counts created timers, so a test advances time only once the starter's retry delay exists.</summary>
internal sealed class StorageMigrationStarterCountingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private int _timers;
    public int Timers => Volatile.Read(ref _timers);

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref _timers);
        return timer;
    }
}
