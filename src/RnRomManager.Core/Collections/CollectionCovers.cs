using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Collections;

/// <summary>
/// Cover files in <c>Paths:Collections</c> (B10–B11): <c>&lt;collectionId&gt;.png</c> or <c>.jpg</c>. Which one is served
/// is decided by the row's <c>cover_format</c>; these methods only handle files.
/// </summary>
public sealed class CollectionCovers(IOptions<PathsOptions> paths, ILogger<CollectionCovers> logger)
{
    public static readonly IReadOnlyList<string> Formats = ["png", "jpg"];

    /// <summary>
    /// Writes a uniquely named temp file next to the target and renames it over <c>&lt;id&gt;.&lt;format&gt;</c> (B10 steps 1–2).
    /// The caller updates the row, then calls <see cref="DeleteOtherFormat"/>.
    /// </summary>
    /// <returns><c>png</c> or <c>jpg</c>: the value for <c>cover_format</c>.</returns>
    public async Task<string> WriteAsync(Guid id, ImageFormat format, byte[] bytes, CancellationToken cancellationToken)
    {
        var extension = ImageFormats.Extension(format);
        var temp = Path.Combine(paths.Value.Collections, $"{id:D}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            File.Move(temp, PathOf(id, extension), overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return extension;
    }

    /// <summary>Deletes the other format's file, if any (B10 step 4).</summary>
    public void DeleteOtherFormat(Guid id, string keep)
    {
        foreach (var format in Formats.Where(f => f != keep))
            Delete(id, format);
    }

    /// <summary>Deletes both possible files (B5, B11).</summary>
    public void DeleteAll(Guid id)
    {
        foreach (var format in Formats)
            Delete(id, format);
    }

    /// <summary>Best effort: a missing file is a no-op, a failure is logged as a warning.</summary>
    public void Delete(Guid id, string format)
    {
        if (!TryDelete(PathOf(id, format)))
            logger.LogWarning("Couldn't delete the cover file {Path}", PathOf(id, format));
    }

    /// <summary>The cover file for reading, or null when it's missing (e.g. after a database restore).</summary>
    public FileStream? Open(Guid id, string format)
    {
        try
        {
            return new FileStream(PathOf(id, format), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, useAsync: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    // format comes from the row or Formats, never from a request, so it can't escape the folder.
    private string PathOf(Guid id, string format) => Path.Combine(paths.Value.Collections, $"{id:D}.{format}");

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path); // no-op when missing
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
