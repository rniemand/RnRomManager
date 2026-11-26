namespace RnRomManager.Core.Library;

/// <summary>One mirrored claim on an external ROM file: <paramref name="ExternalFileName"/> in the folder of <paramref name="ZipPath"/>.</summary>
/// <param name="ZipPath">The owning game's ZIP, relative to <c>Paths:Roms</c>, <c>/</c> separators.</param>
/// <param name="SizeBytes">The recorded size, not the size on disk.</param>
/// <param name="FileId">B13: the <c>rom_files</c> id of a set file; null for a main file.</param>
public sealed record ExternalRomRow(
    Guid GameId,
    string ZipPath,
    string ExternalFileName,
    long SizeBytes,
    string Sha1,
    long? FileId = null);
