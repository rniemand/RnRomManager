namespace RnRomManager.Core.Library;

/// <summary>
/// Outcome of a library edit. <c>Code</c> is null on success, else a §23.1 code: <c>NotFound</c>, <c>JobRunning</c>,
/// <c>InfoJsonNewerSchema</c>, <c>TargetZipUnreadable</c> (409s) or <c>IoError</c>, <c>VerifyFailed</c>,
/// <c>DbWriteFailed</c> (500s).
/// </summary>
public sealed record LibraryEditResult(string? Code = null, string? Details = null)
{
    public const string NotFound = "NotFound";
    public const string JobRunning = "JobRunning";
    public const string IoError = "IoError";
    public const string VerifyFailed = "VerifyFailed";
    public const string DbWriteFailed = "DbWriteFailed";

    public static LibraryEditResult Ok { get; } = new();
}
