namespace RnRomManager.Core.Library;

/// <summary><c>roms[].metadata</c> (spec §6.2). Hashes are lowercase hex.</summary>
/// <param name="DateModified">Last time this entry's bytes were (re)written (ingest or self-repair).</param>
/// <param name="SourcePath">Ingest path at ingestion time (the archive's path for an archive entry).</param>
public sealed record InfoJsonRomMetadata(
    long SizeBytes,
    string Crc32,
    string Md5,
    string Sha1,
    DateTimeOffset DateAdded,
    DateTimeOffset DateModified,
    string SourcePath);
