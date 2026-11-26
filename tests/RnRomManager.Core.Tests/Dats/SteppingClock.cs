namespace RnRomManager.Core.Tests.Dats;

internal sealed class SteppingClock(DateTimeOffset start) : TimeProvider
{
    private long _readings;

    public override DateTimeOffset GetUtcNow() => start.AddSeconds(Interlocked.Increment(ref _readings));
}
