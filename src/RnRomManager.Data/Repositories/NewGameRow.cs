namespace RnRomManager.Data.Repositories;

internal sealed class NewGameRow
{
    public Guid Id { get; init; }
    public string DisplayTitle { get; init; } = "";
    public string SystemId { get; init; } = "";
    public DateTime AddedAt { get; init; }
    public long ReleaseCount { get; init; }
}
