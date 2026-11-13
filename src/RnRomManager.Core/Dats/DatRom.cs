namespace RnRomManager.Core.Dats;

/// <summary>One ROM of a no-intro DAT. Hashes are lowercase hex (spec §6.2).</summary>
/// <param name="Serial">The DAT's <c>serial</c> (rom block, else game block), as written; may list several values separated by <c>,</c> (B3).</param>
public sealed record DatRom(string GameName, string RomName, long SizeBytes, string Crc32, string Md5, string Sha1, string? Serial = null);
