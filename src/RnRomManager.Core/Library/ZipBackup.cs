using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Library;

/// <summary>A verified backup of a source ZIP (spec W3): where it is, the file it is, and its SHA256.</summary>
public sealed record ZipBackup(string Path, ExternalFileSnapshot Snapshot, string Sha256);
