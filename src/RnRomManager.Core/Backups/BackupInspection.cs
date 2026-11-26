using System.IO.Compression;

namespace RnRomManager.Core.Backups;

/// <summary>
/// The outcome of <see cref="BackupCheck.Open"/>: the open archive and its manifest, or why it can't be restored. The
/// caller disposes <see cref="Zip"/> when it is set.
/// </summary>
public sealed record BackupInspection(
    ZipArchive? Zip,
    BackupManifest? Manifest,
    BackupCheckFailure? Failure,
    string? Message);
