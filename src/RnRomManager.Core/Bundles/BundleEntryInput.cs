namespace RnRomManager.Core.Bundles;

/// <summary>An entry as the client sent it (B4): every field may be missing or malformed until validated.</summary>
public sealed record BundleEntryInput(
    string? GameId,
    string? ReleaseSha1,
    BundleMemberInput? Member,
    string? GameTitle,
    string? FileName);
