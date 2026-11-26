namespace RnRomManager.Core.Ingest;

/// <summary>One ROM entry extracted from an archive.</summary>
/// <param name="EntryPath">Full path inside the archive (<c>archive_entry</c>), <c>/</c>-separated.</param>
/// <param name="ExtractedPath">Where it was written under the work dir.</param>
public sealed record ExtractedEntry(string EntryPath, string ExtractedPath, long SizeBytes)
{
    /// <summary>The file-name part: <c>originalFileName</c> (§7.3).</summary>
    public string FileName => EntryPath[(EntryPath.LastIndexOf('/') + 1)..];
}
