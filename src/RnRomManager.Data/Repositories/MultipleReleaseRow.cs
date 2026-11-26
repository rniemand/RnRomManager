namespace RnRomManager.Data.Repositories;

internal sealed class MultipleReleaseRow
{
    public Guid Id { get; init; }
    public string DisplayTitle { get; init; } = "";
    public string SystemId { get; init; } = "";
    public long ReleaseCount { get; init; }
    public long UpdateCount { get; init; }
    public long Bytes { get; init; }
}
