namespace RnRomManager.Core.Library;

/// <summary>One <c>saves/</c> entry to write: its declared size, new bytes (verified by <paramref name="Sha1"/>) or null to copy it.</summary>
internal sealed record SaveEntryPlan(
    string Name,
    long Size,
    Func<Stream>? New = null,
    string? Sha1 = null);
