using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using static RnRomManager.Core.Integrity.IntegrityIssueType;

namespace RnRomManager.Core.Integrity;
/// <summary>C5: the image cache part of <see cref="IntegrityJob"/>: missing covers are generated, everything else removed.</summary>
internal sealed class ImageCacheSweep(
    IImageRepository images,
    CoverCache cache,
    IOptions<PathsOptions> paths,
    InfoJsonReader reader,
    TimeProvider time,
    ILogger logger)
{
    /// <summary>
    /// C5: generates each missing cached cover, then (unless cancelled) deletes everything under the cache that isn't a
    /// current cover, a <c>.tmp</c> younger than <see cref="IntegrityJob.TmpGrace"/>, or the startup probe. A game's failure is logged and counted.
    /// </summary>
    internal async Task<(int Generated, int Removed, int Failed)> RefreshImageCacheAsync(CancellationToken ct)
    {
        var covers = await images.ListCoversAsync(CancellationToken.None).ConfigureAwait(false);
        int generated = 0, failed = 0;
        foreach (var cover in covers)
        {
            if (ct.IsCancellationRequested)
                return (generated, 0, failed);
            if (cache.CachedPath(cover.GameId, cover.Sha1) is not null)
                continue;
            try
            {
                using var zip = IntegrityJob.OpenChecked(paths.Value.Roms, cover.ZipPath, reader);
                await using var stream = zip.OpenEntry(LibraryZip.ImageEntry(cover.FileName), ImageFormats.MaxBytes);
                var result = await cache.GenerateAsync(cover.GameId, cover.Sha1, stream, ct).ConfigureAwait(false);
                if (result.Stored)
                    generated++;
                else
                {
                    failed++;
                    if (result.Webp is null)
                        logger.LogWarning("Image cache: the cover of game {GameId} couldn't be converted to WebP", cover.GameId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (generated, 0, failed);
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning("Image cache: the cover of game {GameId} in {Zip} couldn't be cached: {Message}", cover.GameId, cover.ZipPath, ex.Message);
            }
        }
        return (generated, SweepImageCache(covers.ToDictionary(c => c.GameId, c => c.Sha1)), failed);
    }

    /// <returns>How many files (and links) were deleted.</returns>
    internal int SweepImageCache(IReadOnlyDictionary<Guid, string> current)
    {
        var root = new DirectoryInfo(cache.Root);
        if (!root.Exists)
            return 0;
        var tmpCutoff = time.GetUtcNow().UtcDateTime - IntegrityJob.TmpGrace;
        var removed = 0;
        foreach (var entry in root.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo { LinkTarget: null } folder && Guid.TryParseExact(folder.Name, "D", out var id)
                && folder.Name == id.ToString("D") && current.TryGetValue(id, out var sha1))
            {
                foreach (var inner in folder.EnumerateFileSystemInfos())
                {
                    var fresh = inner is FileInfo { LinkTarget: null } && inner.Name.EndsWith(".tmp", StringComparison.Ordinal) && inner.LastWriteTimeUtc > tmpCutoff;
                    if (!fresh && !(inner is FileInfo { LinkTarget: null } && inner.Name == sha1 + CoverCache.Extension))
                        removed += Remove(inner);
                }
                continue;
            }
            if (entry is FileInfo && entry.Name == StorageFolders.ProbeFileName)
                continue;
            removed += Remove(entry);
        }
        return removed;
    }

    /// <summary>Deletes a file, a link (never its target) or a folder tree.</summary>
    private int Remove(FileSystemInfo entry)
    {
        try
        {
            if (entry is DirectoryInfo { LinkTarget: null } folder)
            {
                // Recursion doesn't enter links, and Delete(true) removes a link inside without following it.
                var files = folder.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                    .Count(e => e is FileInfo || e.LinkTarget is not null);
                folder.Delete(recursive: true);
                return files;
            }
            entry.Delete();
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Image cache: {Path} couldn't be deleted: {Message}", entry.FullName, ex.Message);
            return 0;
        }
    }
}
