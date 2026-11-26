namespace RnRomManager.Core.Library;

/// <summary>C5: a game's cover image and the ZIP holding it (relative to <c>Paths:Roms</c>).</summary>
public sealed record CoverRow(
    Guid GameId,
    string ZipPath,
    string FileName,
    string Sha1);
