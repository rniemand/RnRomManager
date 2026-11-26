namespace RnRomManager.Core.Library;

/// <summary>C2: the WebP bytes of a generated cover (null when decoding or encoding failed), and whether they were cached.</summary>
public sealed record CoverCacheResult(byte[]? Webp, bool Stored);
