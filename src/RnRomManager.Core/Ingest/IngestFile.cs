using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Ingest;

/// <summary>A file waiting in <c>ingest/&lt;system&gt;/</c>, or a ROM entry extracted from an archive there.</summary>
public sealed record IngestFile(
    string SystemId,
    string FullPath,
    string Name,
    long SizeBytes,
    DateTime ModifiedAtUtc)
{
    /// <summary>Set for an archive entry: the archive in ingest (<c>FullPath</c> is then the extracted copy).</summary>
    public string? ArchivePath { get; init; }

    /// <summary>Set for an archive entry: its full path inside the archive.</summary>
    public string? ArchiveEntry { get; init; }

    /// <summary>
    /// Set for an external archive's entry: the archive as it was extracted. A failed placement deletes the entry's
    /// moved copy only while the archive still matches it (spec I5).
    /// </summary>
    public ExternalFileSnapshot? ArchiveSnapshot { get; init; }

    /// <summary>The ingest path recorded as the source (§6.2 <c>sourcePath</c>): the archive for an entry.</summary>
    public string SourcePath => ArchivePath ?? FullPath;
}
