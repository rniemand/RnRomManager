namespace RnRomManager.Data.Repositories;

internal sealed record BundleRelease(
    long Id,
    Guid GameId,
    string Sha1,
    string FileName,
    string GameTitle);
