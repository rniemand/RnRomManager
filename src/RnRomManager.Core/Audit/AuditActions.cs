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
    public const string PrimarySet = "PrimarySet";
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

    /// <summary>Timeline polish C2: a developer's shared logo was added or replaced, or removed.</summary>
    public const string DeveloperLogoReplaced = "DeveloperLogoReplaced";
    public const string DeveloperLogoRemoved = "DeveloperLogoRemoved";

    /// <summary>B13–B14: a genre's or system's shared logo was added or replaced, or removed.</summary>
    public const string GenreLogoReplaced = "GenreLogoReplaced";
    public const string GenreLogoRemoved = "GenreLogoRemoved";
    public const string SystemLogoReplaced = "SystemLogoReplaced";
    public const string SystemLogoRemoved = "SystemLogoRemoved";

    /// <summary>B27: an Admin restored a backup while the app ran (details <c>&lt;file&gt;; safety backup &lt;file&gt;</c>).</summary>
    public const string BackupRestored = "BackupRestored";

    /// <summary>S6: the storage migration moved a legacy game's ROMs out of its ZIP.</summary>
    public const string RomsExternalized = "RomsExternalized";

    /// <summary>ROM Zip Config B8: the storage job moved a game's ROMs from beside its ZIP into it.</summary>
    public const string RomsEmbedded = "RomsEmbedded";

    /// <summary>ROM Zip Config B7: an Admin's change of a system's ROM storage, applied by the storage job.</summary>
    public const string RomStorageChanged = "RomStorageChanged";

    /// <summary>ZIP Compression Z9: an Admin's change of a system's ZIP compression level, applied by the repack job.</summary>
    public const string ZipCompressionChanged = "ZipCompressionChanged";

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

    /// <summary>UI polish B22: a browser play started (details: the release's file name).</summary>
    public const string GamePlayed = "GamePlayed";

    /// <summary>Release editing B10: an Admin changed a release's tags (details <c>&lt;old&gt; → &lt;new&gt;</c> when renamed).</summary>
    public const string ReleaseEdited = "ReleaseEdited";

    public static readonly IReadOnlyList<string> All =
    [
        Ingested, ZipRepaired, ExtensionRenamed, MovedToFailed, MovedToDuplicates, ArchiveDeleted, ReviewDeleted, GameUpdated,
        ReleaseDeleted, GameDeleted, ImageAdded, ImageDeleted, CoverSet, PrimarySet, MediaFetched, ReleaseMoved, GamesMerged, DatsRefreshed,
        IngestFileDeleted, RemovedRestored, PublisherLogoReplaced, PublisherLogoRemoved, DeveloperLogoReplaced, DeveloperLogoRemoved, GenreLogoReplaced, GenreLogoRemoved,
        SystemLogoReplaced, SystemLogoRemoved, BackupRestored, RomsExternalized, RomsEmbedded, RomStorageChanged, ZipCompressionChanged,
        SaveAdded, SaveUpdated, SaveDeleted, UserCreated, UserTypeChanged, UserDisabled, UserEnabled, PasswordReset,
        PasswordChanged, SignedIn, SignInFailed, LockedOut, GamePlayed, ReleaseEdited,
    ];
}
