namespace RnRomManager.Core.Library;

/// <summary><c>roms[].identification</c> (spec §6.2).</summary>
/// <param name="Method"><c>Dat</c> only for an exact SHA1 match.</param>
/// <param name="DatName">DAT header name; also set for an <c>Unverified</c> release (the entry it was based on).</param>
/// <param name="DatVersion">DAT header version (D25).</param>
/// <param name="DatGameName">DAT <c>game.name</c>, the thumbnail key.</param>
public sealed record InfoJsonIdentification(
    IdentificationMethod Method,
    string? DatName = null,
    string? DatVersion = null,
    string? DatGameName = null);
