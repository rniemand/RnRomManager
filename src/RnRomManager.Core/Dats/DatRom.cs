namespace RnRomManager.Core.Dats;

/// <summary>One ROM of a no-intro DAT. Hashes are lowercase hex (spec §6.2).</summary>
public sealed record DatRom(string GameName, string RomName, long SizeBytes, string Crc32, string Md5, string Sha1);
