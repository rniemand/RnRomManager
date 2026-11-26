namespace RnRomManager.Data.Repositories;

internal sealed class MissingMetadataRow
{
    public Guid Id { get; init; }
    public string DisplayTitle { get; init; } = "";
    public string SystemId { get; init; } = "";
    public int? ReleaseYear { get; init; }
    public IReadOnlyList<string> Developers { get; init; } = [];
    public IReadOnlyList<string> Publishers { get; init; } = [];
}
