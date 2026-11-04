namespace RnRomManager.Core.Dats;

/// <summary>A parsed no-intro DAT: header <c>name</c> + <c>version</c> (D25) and its ROMs in file order.</summary>
public sealed record NoIntroDat(string DatName, string? Version, IReadOnlyList<DatRom> Roms);
