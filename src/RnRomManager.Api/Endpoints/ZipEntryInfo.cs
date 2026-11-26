namespace RnRomManager.Api.Endpoints;

/// <summary>One file entry of a library ZIP (Z2): sizes from the central directory, the DOS time read as UTC.</summary>
public sealed record ZipEntryInfo(
    string Path,
    long SizeBytes,
    long CompressedSizeBytes,
    DateTime ModifiedUtc);
