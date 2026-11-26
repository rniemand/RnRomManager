namespace RnRomManager.Core.Ingest;

/// <summary>Arcade I2: one file of a set ZIP. <see cref="Crc32"/> is 8 lower-case hex digits.</summary>
public sealed record MameEntry(string Name, string Crc32);
