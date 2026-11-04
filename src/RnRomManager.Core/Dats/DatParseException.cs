namespace RnRomManager.Core.Dats;

/// <summary>A DAT file can't be parsed: the whole file (and its system's DAT set) counts as corrupt (spec §10.1).</summary>
public sealed class DatParseException(string message) : Exception(message);
