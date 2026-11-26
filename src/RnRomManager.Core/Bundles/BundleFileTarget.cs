namespace RnRomManager.Core.Bundles;

/// <summary>
/// The current download target of a resolved identity (B3, B5): today's numeric ids, the game's display title and the
/// canonical attachment name. <see cref="FileId"/> is the <c>rom_files</c> id of a set file, null for a main file.
/// </summary>
public sealed record BundleFileTarget(
    Guid GameId,
    string GameTitle,
    long RomId,
    long? FileId,
    string FileName)
{
    /// <summary>The normal same-origin single-file download route for this target.</summary>
    public string DownloadUrl => FileId is { } fileId
        ? $"/api/games/{GameId}/roms/{RomId}/files/{fileId}/download"
        : $"/api/games/{GameId}/roms/{RomId}/download";
}
