namespace RnRomManager.Core.Integrity;

/// <summary>One problem (or DB repair) an integrity run found; deduplicated on (<c>Type</c>, <c>ZipPath</c>, <c>EntryName</c>).</summary>
/// <param name="ZipPath">Relative to <c>Paths:Roms</c>, <c>/</c> separators; for <c>OrphanFile</c> the orphan itself.</param>
public sealed record IntegrityFinding(string Type, string? ZipPath, string? EntryName = null, string? SystemId = null, Guid? GameId = null,
    string? Details = null)
{
    /// <summary>Paths compare case-insensitively (§5); so do external file names (G2a), ZIP entry names don't.</summary>
    public (string Type, string ZipPath, string EntryName) Key => KeyOf(Type, ZipPath, EntryName);

    public static (string Type, string ZipPath, string EntryName) KeyOf(string type, string? zipPath, string? entryName) =>
        (type, zipPath?.ToUpperInvariant() ?? "", IsPointerIssue(type, entryName) ? entryName!.ToUpperInvariant() : entryName ?? "");

    /// <summary>
    /// G2a: a missing, wrong-length or wrong-hash finding about an external file, whose entry name is the file name
    /// (no <c>/</c>); a ZIP entry's always has one (<c>roms/…</c>, <c>images/…</c>).
    /// </summary>
    public static bool IsPointerIssue(string type, string? entryName) =>
        type is IntegrityIssueType.EntryMissing or IntegrityIssueType.EntrySizeMismatch or IntegrityIssueType.HashMismatch
        && !string.IsNullOrEmpty(entryName) && !entryName.Contains('/');
}
