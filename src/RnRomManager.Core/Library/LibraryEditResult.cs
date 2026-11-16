namespace RnRomManager.Core.Library;

/// <summary>
/// Outcome of a library edit. <c>Code</c> is null on success, else a §23.1 code: <c>ValidationFailed</c> (400, with
/// <c>Errors</c>), <c>NotFound</c>, <c>JobRunning</c>, <c>InfoJsonNewerSchema</c>, <c>TargetZipUnreadable</c> (409s) or
/// <c>IoError</c>, <c>VerifyFailed</c>, <c>DbWriteFailed</c> (500s). Image edits add <c>TooManyImages</c>, <c>DuplicateImage</c>
/// and <c>DefaultImage</c> (409s). <c>GameId</c> is the game the edit ended in (move/merge).
/// </summary>
public sealed record LibraryEditResult(string? Code = null, string? Details = null)
{
    public const string ValidationFailed = "ValidationFailed";
    public const string NotFound = "NotFound";
    public const string JobRunning = "JobRunning";
    public const string IoError = "IoError";
    public const string VerifyFailed = "VerifyFailed";
    public const string DbWriteFailed = "DbWriteFailed";
    public const string TooManyImages = "TooManyImages";
    public const string DuplicateImage = "DuplicateImage";
    public const string DefaultImage = "DefaultImage";

    /// <summary>B16/B18: a FileSets file keeps its exact name, and that name is already used where it has to go (409).</summary>
    public const string NameTaken = "NameTaken";

    /// <summary>S3: a legacy game's ROMs are still inside its ZIP; every write waits for the storage migration (409).</summary>
    public const string StorageMigrationPending = "StorageMigrationPending";

    /// <summary>S3: the ProblemDetails title of <see cref="StorageMigrationPending"/>.</summary>
    public const string StorageMigrationPendingTitle =
        "This game's ROMs are being moved out of its ZIP. Try again when the storage migration job has finished.";

    public static LibraryEditResult Ok { get; } = new();

    public Guid? GameId { get; init; }

    public Dictionary<string, string[]>? Errors { get; init; }

    /// <summary>
    /// W4: an external merge's target ZIP was replaced after it was written, so the source ZIP was kept and its
    /// releases didn't move, even when the code is <see cref="DbWriteFailed"/>.
    /// </summary>
    public bool TargetPublicationLost { get; init; }

    /// <summary>B14: the added box art replaced the placeholder that was the game's cover.</summary>
    public bool ReplacedDefaultCover { get; init; }

    public static LibraryEditResult Invalid(string field, string error) =>
        new(ValidationFailed) { Errors = new() { [field] = [error] } };
}
