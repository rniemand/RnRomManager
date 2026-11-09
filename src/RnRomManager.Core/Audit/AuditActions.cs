namespace RnRomManager.Core.Audit;

/// <summary>AU4: the audited file actions, one writer each.</summary>
public static class AuditActions
{
    public const string Ingested = "Ingested";
    public const string ZipRepaired = "ZipRepaired";
    public const string ExtensionRenamed = "ExtensionRenamed";
    public const string MovedToFailed = "MovedToFailed";
    public const string MovedToDuplicates = "MovedToDuplicates";
    public const string ArchiveDeleted = "ArchiveDeleted";
    public const string ReviewDeleted = "ReviewDeleted";
    public const string GameUpdated = "GameUpdated";
    public const string ReleaseDeleted = "ReleaseDeleted";
    public const string GameDeleted = "GameDeleted";
    public const string ImageAdded = "ImageAdded";
    public const string ImageDeleted = "ImageDeleted";
    public const string CoverSet = "CoverSet";
    public const string MediaFetched = "MediaFetched";
    public const string ReleaseMoved = "ReleaseMoved";
    public const string GamesMerged = "GamesMerged";
    public const string DatsRefreshed = "DatsRefreshed";
    public const string IngestFileDeleted = "IngestFileDeleted";

    public static readonly IReadOnlyList<string> All =
    [
        Ingested, ZipRepaired, ExtensionRenamed, MovedToFailed, MovedToDuplicates, ArchiveDeleted, ReviewDeleted, GameUpdated,
        ReleaseDeleted, GameDeleted, ImageAdded, ImageDeleted, CoverSet, MediaFetched, ReleaseMoved, GamesMerged, DatsRefreshed,
        IngestFileDeleted,
    ];
}
