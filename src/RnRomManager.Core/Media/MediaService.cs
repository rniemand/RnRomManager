using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Media;

/// <summary>A box art or snap ready to store as <c>images/&lt;fileName&gt;</c>.</summary>
/// <param name="Source">The <c>info.json</c> source of a fetched image; null means libretro-thumbnails.</param>
public sealed record MediaImage(ImageType Type, byte[] Bytes, int Width, int Height, string? SourceUrl, bool IsDefault, string? Source = null)
{
    public const string BoxArtFileName = "boxart-01.png";
    public const string SnapFileName = "snap-01.png";

    /// <summary>T8: <c>png</c> or <c>jpg</c>, from the magic bytes.</summary>
    public string Extension => ImageFormats.Extension(ImageFormats.Detect(Bytes) ?? ImageFormat.Png);

    /// <summary>The name of a new game's first image of this type, by format (<c>boxart-01.jpg</c>).</summary>
    public string FileName => $"{GameImages.Prefix(Type)}-01.{Extension}";

    public InfoJsonImage ToInfoJson(DateTimeOffset dateAdded, bool isCover) => new()
    {
        FileName = FileName,
        Type = Type,
        Source = IsDefault ? InfoJsonImage.SourceDefault : Source ?? InfoJsonImage.SourceLibretro,
        SourceUrl = SourceUrl,
        IsDefault = IsDefault,
        IsCover = isCover,
        SizeBytes = Bytes.Length,
        Sha1 = Convert.ToHexStringLower(SHA1.HashData(Bytes)),
        Width = Width,
        Height = Height,
        DateAdded = dateAdded,
    };
}

/// <param name="BoxArt">Always set: the handler's default when nothing was found.</param>
public sealed record MediaResult(MediaImage BoxArt, MediaImage? Snap);

/// <summary>
/// Box art + snap lookup on libretro-thumbnails (spec §10.2). Names are tried in the order given (primary release first,
/// then primary-rank order; null = a Manual release, skipped); the first valid PNG of each type wins. Network failures
/// feed the job's <see cref="CircuitBreaker"/>; once it's open nothing more is requested. A <c>TitleDb</c> system (T8)
/// instead takes each name's entry (by the id its <c>datGameName</c> ends with) and fetches its own CDN URLs, PNG or
/// JPEG, through <see cref="TitleDbImageClient"/>; an empty <c>Sources:ThumbnailBaseUrl</c> disables both.
/// </summary>
public sealed class MediaService(HttpClient http, TitleDbImageClient titleDbImages, DatCache dats, IOptions<SourcesOptions> sources,
    ILogger<MediaService> logger)
{
    /// <summary>libretro thumbnails keep the original 5 MiB cap (I1a); stored images may be larger.</summary>
    public const int MaxThumbnailBytes = 5 * 1024 * 1024;

    public async Task<MediaResult> FindAsync(
        ISystemHandler handler, IEnumerable<string?> datGameNames, CircuitBreaker breaker, CancellationToken cancellationToken)
    {
        var names = datGameNames.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var baseUrl = sources.Value.ThumbnailBaseUrl;
        if (string.IsNullOrEmpty(baseUrl) || names.Count == 0)
            return new MediaResult(Default(handler), null);

        var titleDb = handler.DatSource == DatSource.TitleDb;
        var boxArt = await FirstAsync(ImageType.BoxArt, ThumbnailKey.BoxArtFolder).ConfigureAwait(false);
        var snap = await FirstAsync(ImageType.Snap, ThumbnailKey.SnapFolder).ConfigureAwait(false);
        return new MediaResult(boxArt ?? Default(handler), snap);

        async Task<MediaImage?> FirstAsync(ImageType type, string folder)
        {
            foreach (var name in names)
            {
                if (breaker.IsOpen)
                    return null;
                var url = titleDb ? TitleDbUrl(handler, name, type) : ThumbnailKey.Url(baseUrl, handler.DatName, folder, name);
                // T8: the CDN rule is checked again right before every request.
                if (url is null || (titleDb && !TitleDb.IsCdnUrl(url)))
                    continue;
                if (await GetImageAsync(titleDb ? titleDbImages.Http : http, url, allowJpeg: titleDb, breaker, cancellationToken).ConfigureAwait(false) is { } image)
                    return new MediaImage(type, image.Bytes, image.Width, image.Height, url, IsDefault: false,
                        titleDb ? InfoJsonImage.SourceTitleDb : null);
            }
            return null;
        }
    }

    /// <summary>T8: the box-art or snap URL of the titledb entry a <c>datGameName</c> names; null when there is none.</summary>
    private string? TitleDbUrl(ISystemHandler handler, string datGameName, ImageType type) =>
        TitleDb.TitleIdOfGameName(datGameName) is { } titleId && dats.For(handler.Id)?.ByTitleId(titleId) is { } entry
            ? type == ImageType.BoxArt ? entry.BoxArtUrl : entry.SnapUrl
            : null;

    public static MediaImage Default(ISystemHandler handler)
    {
        var bytes = handler.DefaultBoxArtPng.ToArray();
        var info = PngInfo.Read(bytes) ?? throw new InvalidOperationException($"Default box art of '{handler.Id}' is not a valid PNG.");
        return new MediaImage(ImageType.BoxArt, bytes, info.Width, info.Height, null, IsDefault: true);
    }

    /// <param name="client">libretro's or the titledb CDN's; a 3xx (redirects are off for the CDN) is a neutral miss like a 404.</param>
    /// <param name="allowJpeg">titledb images may be JPEG; libretro thumbnails stay PNG-only.</param>
    private async Task<(byte[] Bytes, int Width, int Height)?> GetImageAsync(HttpClient client, string url, bool allowJpeg, CircuitBreaker breaker,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                Failed(url, $"HTTP {(int)response.StatusCode}", breaker);
                return null;
            }
            // §10.2: a 404 neither counts nor resets; a 200 resets.
            if (response.StatusCode != HttpStatusCode.OK)
                return null;
            breaker.RecordSuccess();

            await using var body = new LimitStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), MaxThumbnailBytes);
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }
        catch (LimitExceededException)
        {
            logger.LogWarning("Thumbnail {Url} is larger than {Max} bytes; ignored", url, MaxThumbnailBytes);
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Connection error or timeout, after the client's retries.
            Failed(url, ex.Message, breaker);
            return null;
        }

        if (PngInfo.Read(bytes) is { } png)
            return (bytes, png.Width, png.Height);
        if (allowJpeg && JpegInfo.Read(bytes) is { } jpeg)
            return (bytes, jpeg.Width, jpeg.Height);
        logger.LogWarning("Thumbnail {Url} is not a valid {Formats}; ignored", url, allowJpeg ? "PNG or JPEG" : "PNG");
        return null;
    }

    private void Failed(string url, string error, CircuitBreaker breaker)
    {
        logger.LogDebug("Thumbnail {Url} failed: {Error}", url, error);
        if (breaker.RecordFailure())
            logger.LogWarning("Thumbnail host unreachable ({Count} failures in a row, last: {Error}); media lookups skipped for the rest of this job",
                CircuitBreaker.Threshold, error);
    }
}
