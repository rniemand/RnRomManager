namespace RnRomManager.Core.Dats;

/// <summary>A parsed metadat file (releaseyear/developer/publisher/genre). Metadat headers have no version.</summary>
public sealed record MetadatFile(string DatName, string Field, IReadOnlyList<MetadatValue> Values);
