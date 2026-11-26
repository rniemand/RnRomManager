namespace RnRomManager.Core.Library;

/// <summary>
/// A row of <c>rom_files</c> (B12): one <c>files</c> entry of a release. <see cref="RomSha1"/> is the release's main
/// <c>sha1</c>, by which the mirror finds <see cref="RomId"/> (<c>ux_roms_sha1</c>).
/// </summary>
public sealed record RomFileRow
{
    public long Id { get; init; }
    public long RomId { get; init; }
    public required Guid GameId { get; init; }
    public required string RomSha1 { get; init; }

    /// <summary>The entry's 0-based index in <c>files</c>; 0 is the cue, <c>.toc</c>, <c>.ccd</c> or <c>.mds</c>.</summary>
    public required int Ordinal { get; init; }

    public required string FileName { get; init; }
    public required string ExternalFileName { get; init; }
    public required string OriginalFileName { get; init; }
    public required long SizeBytes { get; init; }
    public required string Crc32 { get; init; }
    public required string Md5 { get; init; }
    public required string Sha1 { get; init; }
}
