namespace RnRomManager.Core.Ingest;

/// <summary>§7.2 reason codes for <c>Error</c> items (spec §23.1).</summary>
public static class IngestErrors
{
    public const string IoError = "IoError";
    public const string VerifyFailed = "VerifyFailed";
    public const string DbWriteFailed = "DbWriteFailed";
    public const string TargetZipUnreadable = "TargetZipUnreadable";
    public const string InfoJsonNewerSchema = "InfoJsonNewerSchema";
    public const string Unexpected = "Unexpected";

    /// <summary>B10: a <c>FileSets</c> file's exact name is already used in its folder or by the game; nothing moved.</summary>
    public const string NameTaken = "NameTaken";

    /// <summary>B10: a <c>FileSets</c> file's exact name is over 250 UTF-8 bytes; it is never cut.</summary>
    public const string NameTooLong = "NameTooLong";

    /// <summary>S3: the target is a legacy game whose ROMs are still inside its ZIP; the file stays in ingest for the next import.</summary>
    public const string StorageMigrationPending = "StorageMigrationPending";
}
