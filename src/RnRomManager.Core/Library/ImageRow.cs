namespace RnRomManager.Core.Library;

/// <summary>A row of <c>images</c> (spec §14.1).</summary>
public sealed record ImageRow
{
    public long Id { get; init; }
    public required Guid GameId { get; init; }
    public required string FileName { get; init; }
    public required ImageType Type { get; init; }
    public required string Source { get; init; }
    public string? SourceUrl { get; init; }
    public bool IsDefault { get; init; }
    public bool IsCover { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha1 { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required DateTime DateAdded { get; init; }
}
