namespace RnRomManager.Core.Jobs;

public enum JobType
{
    Ingest,
    Integrity,
    DatRefresh,
    MediaRetry,
    ReviewResolve,

    /// <summary>S4: moves legacy games' embedded ROMs next to their ZIPs.</summary>
    StorageMigration,

    /// <summary>ZIP Compression Z9: rewrites every game ZIP of one system at its compression level.</summary>
    ZipRepack,

    /// <summary>Release editing B19: moves version 1 saves into per-user folders, once.</summary>
    SaveFolders,

    /// <summary>Admin actions P1: makes each IGDB-linked game's IGDB cover its cover.</summary>
    PullCovers,

    /// <summary>Admin actions R1: stores each IGDB-linked game's IGDB rating.</summary>
    IgdbSync,
}
