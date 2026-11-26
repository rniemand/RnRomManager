namespace RnRomManager.Core.Ingest;

/// <summary>B7: one member of a bin/cue set on its way beside the ZIP, with its own SHA1.</summary>
internal sealed record SetPlacement(IngestFile File, ExternalPlacement Placement, string Sha1);
