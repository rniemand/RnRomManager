namespace RnRomManager.Core.Ingest;

/// <summary>D2: one file to delete; <see cref="Folder"/> is <c>pending</c> (<c>ingest/&lt;system&gt;/</c>), <c>failed</c> (its <c>_failed/</c>) or <c>removed</c> (its <c>_removed/</c>, Deleted ROMs D4).</summary>
public sealed record IngestDeleteRef(string? System, string? Folder, string? Name);
