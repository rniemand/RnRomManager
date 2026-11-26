namespace RnRomManager.Core.Storage;

/// <summary>A regular file found by <see cref="ExternalRomPath.ScanFolder"/>.</summary>
/// <param name="RelativePath">Relative to the root, <c>/</c> separators, each name as spelled on disk.</param>
public sealed record CheckedFile(string RelativePath, string FullPath, ExternalFileSnapshot Snapshot);
