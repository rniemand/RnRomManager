namespace RnRomManager.Core.Dats;

/// <summary>One <c>field "value"</c> of a metadat file, keyed by ROM CRC (lowercase).</summary>
public sealed record MetadatValue(string Crc32, string Value);
