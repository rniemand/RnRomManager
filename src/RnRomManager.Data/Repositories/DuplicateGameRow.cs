namespace RnRomManager.Data.Repositories;

internal sealed class DuplicateGameRow
{
    public Guid Id { get; init; }
    public string DisplayTitle { get; init; } = "";
    public long RomCount { get; init; }
    public long ZipSizeBytes { get; init; }
    public DateTime CreatedAt { get; init; }
    public long? BoxArtImageId { get; init; }
    public string? BoxArtSha1 { get; init; }
}
