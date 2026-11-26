namespace RnRomManager.Data.Repositories;

internal sealed class GameSizeRow
{
    public Guid Id { get; init; }
    public string DisplayTitle { get; init; } = "";
    public long Bytes { get; init; }
    public long RomCount { get; init; }
    public long UpdateCount { get; init; }
}
