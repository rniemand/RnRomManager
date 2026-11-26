namespace RnRomManager.Core.Library;

public enum DbSyncChange
{
    Inserted,

    /// <summary>Changed ZIP, or the same <c>gameId</c> found at another path (<c>zip_path</c> updated).</summary>
    Updated,

    Deleted,
}
