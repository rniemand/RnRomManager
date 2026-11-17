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

    /// <summary>B13–B14: a genre's or system's shared logo was added or replaced, or removed.</summary>
    public const string GenreLogoReplaced = "GenreLogoReplaced";
    public const string GenreLogoRemoved = "GenreLogoRemoved";
    public const string SystemLogoReplaced = "SystemLogoReplaced";
    public const string SystemLogoRemoved = "SystemLogoRemoved";

    /// <summary>S6: the storage migration moved a legacy game's ROMs out of its ZIP.</summary>
    public const string RomsExternalized = "RomsExternalized";

    /// <summary>B13: a save was added, renamed/re-described or replaced, or deleted.</summary>
    public const string SaveAdded = "SaveAdded";
    public const string SaveUpdated = "SaveUpdated";
    public const string SaveDeleted = "SaveDeleted";

    /// <summary>User Management U9: admin user operations (details <c>user=&lt;name&gt;</c> plus the facts).</summary>
    public const string UserCreated = "UserCreated";
    public const string UserTypeChanged = "UserTypeChanged";
    public const string UserDisabled = "UserDisabled";
    public const string UserEnabled = "UserEnabled";

    /// <summary>U9: an admin or the CLI (<c>via=cli</c>, no actor) reset a user's password.</summary>
    public const string PasswordReset = "PasswordReset";

    /// <summary>U9: account events; the actor is the user (an unknown name for <see cref="SignInFailed"/> has no id).</summary>
    public const string PasswordChanged = "PasswordChanged";
    public const string SignedIn = "SignedIn";
    public const string SignInFailed = "SignInFailed";
    public const string LockedOut = "LockedOut";

    public static readonly IReadOnlyList<string> All =
    [
        Ingested, ZipRepaired, ExtensionRenamed, MovedToFailed, MovedToDuplicates, ArchiveDeleted, ReviewDeleted, GameUpdated,
        ReleaseDeleted, GameDeleted, ImageAdded, ImageDeleted, CoverSet, MediaFetched, ReleaseMoved, GamesMerged, DatsRefreshed,
        IngestFileDeleted, RemovedRestored, PublisherLogoReplaced, PublisherLogoRemoved, GenreLogoReplaced, GenreLogoRemoved,
        SystemLogoReplaced, SystemLogoRemoved, RomsExternalized,
        SaveAdded, SaveUpdated, SaveDeleted, UserCreated, UserTypeChanged, UserDisabled, UserEnabled, PasswordReset,
        PasswordChanged, SignedIn, SignInFailed, LockedOut,
    ];
}
