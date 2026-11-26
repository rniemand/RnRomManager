namespace RnRomManager.Core.Storage;

/// <summary>The result of classifying one path.</summary>
/// <param name="Path">
/// The full path with the spelling found on disk for every part that exists (a name is matched ignoring case);
/// parts that don't exist keep the requested spelling.
/// </param>
/// <param name="Snapshot">Set for a <see cref="ExternalPathStatus.Found"/> file.</param>
/// <param name="Details">Why, for every status but <see cref="ExternalPathStatus.Found"/>.</param>
public sealed record ExternalPathCheck(
    ExternalPathStatus Status,
    string Path,
    ExternalFileSnapshot? Snapshot = null,
    string? Details = null);
