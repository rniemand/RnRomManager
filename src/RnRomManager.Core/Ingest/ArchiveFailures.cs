namespace RnRomManager.Core.Ingest;

/// <summary>§7.3 archive reason codes (spec §23.1).</summary>
public static class ArchiveFailures
{
    public const string ArchiveUnsafe = "ArchiveUnsafe";
    public const string ArchiveCorrupt = "ArchiveCorrupt";
    public const string ArchiveEncrypted = "ArchiveEncrypted";
}
