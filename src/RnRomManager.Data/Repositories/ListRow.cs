namespace RnRomManager.Data.Repositories;

internal sealed class ListRow
{
    public Guid Id { get; init; }
    public string SystemId { get; init; } = "";
    public string DisplayTitle { get; init; } = "";
    public int? ReleaseYear { get; init; }
    public long RomCount { get; init; }
    public string? RegionKeys { get; init; }
    public long? BoxArtImageId { get; init; }
    public string? BoxArtSha1 { get; init; }
    public long? PrimarySizeBytes { get; init; }
    public long UpdateCount { get; init; }
    public long DlcCount { get; init; }
    public int SaveCount { get; init; }
    public long? PrimaryRomId { get; init; }
    public string? PrimaryRomFileName { get; init; }
    public bool? PrimaryIsExternal { get; init; }
    public bool? PrimaryHasFiles { get; init; }
    public bool? PrimaryEncrypted { get; init; }
    public int? IgdbId { get; init; }
    public int? Rating { get; init; }
    public bool Favourite { get; init; }
    public string Title { get; init; } = "";
    public IReadOnlyList<string> Developers { get; init; } = [];
    public IReadOnlyList<string> Publishers { get; init; } = [];
    public IReadOnlyList<string> Genres { get; init; } = [];
    public string? AddOns { get; init; }
}
