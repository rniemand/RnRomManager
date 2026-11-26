namespace RnRomManager.Api.Endpoints;

/// <summary>Failure codes a ticket can end with.</summary>
public static class ErrorCodes
{
    /// <summary>The identity no longer resolves when the stream opens.</summary>
    public const string Unavailable = "Unavailable";
    /// <summary>The shared opener refused (missing or unsafe file, unreadable ZIP).</summary>
    public const string OpenFailed = "OpenFailed";
    /// <summary>The client went away mid-stream.</summary>
    public const string Aborted = "Aborted";
    /// <summary>No progress for <see cref="BundleDownloadEndpoints.InactivityTimeout"/>.</summary>
    public const string Stalled = "Stalled";
    /// <summary>A read or write failed, or the file ended before its expected length.</summary>
    public const string IoError = "IoError";
}
