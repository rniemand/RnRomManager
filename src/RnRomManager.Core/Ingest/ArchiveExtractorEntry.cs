namespace RnRomManager.Core.Ingest;

internal sealed record ArchiveExtractorEntry(
    string Name,
    bool IsDirectory,
    bool IsEncrypted,
    bool IsLink,
    Func<Stream> Open);
