namespace RnRomManager.Core.Storage;

/// <summary>A <see cref="LimitStream"/> delivered more than its limit.</summary>
public sealed class LimitExceededException(long limit)
    : Exception($"More than {limit} bytes were read.")
{
    public long Limit { get; } = limit;
}
