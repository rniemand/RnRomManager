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
    public const string RemovedRestored = "RemovedRestored";

    /// <summary>Collections discovery B5: a publisher's shared logo override was added or replaced.</summary>
    public const string PublisherLogoReplaced = "PublisherLogoReplaced";

    /// <summary>Collections discovery B5: a publisher's shared logo override was removed.</summary>
    public const string PublisherLogoRemoved = "PublisherLogoRemoved";

    /// <summary>S6: the storage migration moved a legacy game's ROMs out of its ZIP.</summary>
    public const string RomsExternalized = "RomsExternalized";

    /// <summary>B13: a save was added, renamed/re-described or replaced, or deleted.</summary>
    public const string SaveAdded = "SaveAdded";
    public const string SaveUpdated = "SaveUpdated";
    public const string SaveDeleted = "SaveDeleted";

    public static readonly IReadOnlyList<string> All =
    [
        Ingested, ZipRepaired, ExtensionRenamed, MovedToFailed, MovedToDuplicates, ArchiveDeleted, ReviewDeleted, GameUpdated,
        ReleaseDeleted, GameDeleted, ImageAdded, ImageDeleted, CoverSet, MediaFetched, ReleaseMoved, GamesMerged, DatsRefreshed,
        IngestFileDeleted, RemovedRestored, PublisherLogoReplaced, PublisherLogoRemoved, RomsExternalized,
        SaveAdded, SaveUpdated, SaveDeleted,
    ];
}
