namespace RnRomManager.Core.Integrity;

/// <summary>One problem (or DB repair) an integrity run found; deduplicated on (<c>Type</c>, <c>ZipPath</c>, <c>EntryName</c>).</summary>
/// <param name="ZipPath">Relative to <c>Paths:Roms</c>, <c>/</c> separators; for <c>OrphanFile</c> the orphan itself.</param>
public sealed record IntegrityFinding(string Type, string? ZipPath, string? EntryName = null, string? SystemId = null, Guid? GameId = null,
    string? Details = null)
{
    /// <summary>Paths compare case-insensitively (§5).</summary>
    public (string Type, string ZipPath, string EntryName) Key => KeyOf(Type, ZipPath, EntryName);

    public static (string Type, string ZipPath, string EntryName) KeyOf(string type, string? zipPath, string? entryName) =>
        (type, zipPath?.ToUpperInvariant() ?? "", entryName ?? "");
}
