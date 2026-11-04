namespace RnRomManager.Core.Dats;

/// <summary>One file in <c>cache/dats/dats.json</c> (spec §10.1).</summary>
/// <param name="File">Path relative to <c>cache/dats/</c>, <c>/</c>-separated (<see cref="DatKind.RelativePath"/>).</param>
/// <param name="Version">null for metadat files (their headers have no version).</param>
/// <param name="Sha1">Lowercase hex of the file bytes; re-checked on every load.</param>
public sealed record DatManifestEntry(string File, string DatName, string? Version, DateTimeOffset DownloadedAt, string Sha1);
