namespace RnRomManager.Core.Storage;

/// <summary>What one streamed read of a ROM file found. Hashes are lowercase hex.</summary>
/// <param name="Header">The first <c>min(size, <see cref="RomFileHasher.HeaderBytes"/>)</c> bytes, for <c>ReadHeader</c>.</param>
public sealed record RomFileHashes(
    long SizeBytes,
    string Crc32,
    string Md5,
    string Sha1,
    byte[] Header);
