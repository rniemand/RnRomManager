namespace RnRomManager.Core.Ingest;

internal enum HashOutcome
{
    Hashed,

    /// <summary>The file changed: on its handle, at its path, or past the size cap while it was read.</summary>
    Changed,

    /// <summary>An IO failure, or the path is no longer a safe regular file.</summary>
    Failed,
}
