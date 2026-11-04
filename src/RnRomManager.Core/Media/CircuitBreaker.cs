namespace RnRomManager.Core.Media;

/// <summary>
/// One per job (spec §10.2): after <see cref="Threshold"/> consecutive network failures, media lookups are skipped for
/// the rest of the job. A 404 isn't a failure; any 200 resets the count.
/// </summary>
public sealed class CircuitBreaker
{
    public const int Threshold = 3;

    private int _failures;

    public bool IsOpen => _failures >= Threshold;

    /// <returns>True exactly once: when this failure trips the breaker (log the single <c>Warn</c> then).</returns>
    public bool RecordFailure() => ++_failures == Threshold;

    public void RecordSuccess()
    {
        if (!IsOpen)
            _failures = 0;
    }
}
