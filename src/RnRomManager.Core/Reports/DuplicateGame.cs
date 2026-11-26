namespace RnRomManager.Core.Reports;

public sealed record DuplicateGame(
    Guid Id,
    string DisplayTitle,
    int RomCount,
    long ZipSizeBytes,
    DateTime CreatedAt,
    long? BoxArtImageId,
    string? BoxArtSha1 = null);
