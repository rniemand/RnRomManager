namespace RnRomManager.Data.Repositories;

internal sealed class HackedGameRow
{
    public Guid Id { get; init; }
    public string DisplayTitle { get; init; } = "";
    public string SystemId { get; init; } = "";
    public long HackCount { get; init; }
    public long ReleaseCount { get; init; }
    public long Bytes { get; init; }
}
