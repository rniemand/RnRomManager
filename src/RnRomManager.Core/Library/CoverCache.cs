using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using SkiaSharp;

namespace RnRomManager.Core.Library;

/// <summary>
/// Image cache C2: each game's cover, resized to at most <see cref="MaxSide"/> px and encoded WebP, cached at
/// <c>&lt;Paths:ImageCache&gt;/&lt;gameId&gt;/&lt;coverSha1&gt;.webp</c>. A new cover has a new SHA1, so a cached file never goes
/// stale; integrity removes the files no game uses (C5). At most <see cref="MaxConcurrent"/> generations run at once.
/// </summary>
public sealed class CoverCache(IOptions<PathsOptions> paths, ILogger<CoverCache> logger)
{
    public const int MaxSide = 512;
    public const int Quality = 80;
    public const int MaxConcurrent = 2;
    public const string Extension = ".webp";

    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    public string Root => paths.Value.ImageCache;

    /// <summary>Where the cover would be cached; <paramref name="coverSha1"/> is the database's (lowercase hex) value.</summary>
    public string PathFor(Guid gameId, string coverSha1) => Path.Combine(Root, gameId.ToString("D"), coverSha1 + Extension);

    /// <returns>The cached file, or null when it isn't there.</returns>
    public string? CachedPath(Guid gameId, string coverSha1) => PathFor(gameId, coverSha1) is var path && File.Exists(path) ? path : null;

    /// <summary>
    /// Reads <paramref name="cover"/> (at most <see cref="ImageFormats.MaxBytes"/> from the caller), resizes and encodes it,
    /// and caches the result through a temp file moved over the target. Waits for a free slot, cancelled by <paramref name="ct"/>.
    /// </summary>
    public async Task<CoverCacheResult> GenerateAsync(
        Guid gameId,
        string coverSha1,
        Stream cover,
        CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var copy = new MemoryStream();
            await cover.CopyToAsync(copy, ct).ConfigureAwait(false);
            copy.Position = 0;
            if (Encode(copy) is not { } webp)
                return new(null, false);

            var target = PathFor(gameId, coverSha1);
            var tmp = $"{target}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(tmp, webp, CancellationToken.None).ConfigureAwait(false);
                File.Move(tmp, target, overwrite: true);
                return new(webp, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("Caching the cover of game {GameId} at {Path} failed: {Message}", gameId, target, ex.Message);
                TryDelete(tmp);
                return new(webp, false);
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <returns>The WebP, or null when the image can't be decoded, a side is over <see cref="ImageFormats.MaxWebpSide"/>, or encoding fails.</returns>
    internal static byte[]? Encode(Stream image)
    {
        using var codec = SKCodec.Create(image);
        if (codec is null || codec.Info.Width > ImageFormats.MaxWebpSide || codec.Info.Height > ImageFormats.MaxWebpSide)
            return null;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null)
            return null;
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(bitmap.Width, bitmap.Height));
        using var resized = scale < 1
            ? bitmap.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale))),
                new SKSamplingOptions(SKCubicResampler.Mitchell))
            : null;
        using var encoded = (resized ?? bitmap).Encode(SKEncodedImageFormat.Webp, Quality);
        return encoded?.ToArray();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
