using Microsoft.Extensions.Logging;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Ingest;

/// <summary>B7/B12, CloneCD B5, D8: the checked read of a small cue, <c>.toc</c>, <c>.ccd</c> or <c>.mds</c> for <see cref="IngestSetPlanner"/>, shared by ingest and Review.</summary>
internal static class IngestCueReader
{
    private static readonly string[] SheetExtensions = [".cue", ".toc", ".ccd", ".mds"];

    /// <returns>The cue's, <c>.toc</c>'s, <c>.ccd</c>'s or <c>.mds</c>'s bytes, through the same checked open as other ingest reads; null for any other file, one over
    /// <see cref="IngestSetPlanner.MaxCueBytes"/>, or one that can't be read.</returns>
    public static byte[]? Read(string ingestRoot, IngestFile file, ILogger logger)
    {
        if (!SheetExtensions.Any(e => file.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            || file.SizeBytes > IngestSetPlanner.MaxCueBytes)
            return null;
        var opened = ExternalRomPath.OpenRead(ingestRoot, file.FullPath, FileShare.Read);
        if (opened.Status != ExternalPathStatus.Found)
            return null;
        try
        {
            using var stream = opened.Stream!;
            using var buffer = new MemoryStream();
            new LimitStream(stream, IngestSetPlanner.MaxCueBytes).CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LimitExceededException)
        {
            logger.LogWarning("{Path}: the set sheet couldn't be read: {Error}", file.FullPath, ex.Message);
            return null;
        }
    }
}
