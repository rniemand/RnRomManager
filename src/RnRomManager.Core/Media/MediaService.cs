using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Media;

/// <summary>A box art or snap ready to store as <c>images/&lt;fileName&gt;</c>.</summary>
public sealed record MediaImage(ImageType Type, byte[] Bytes, int Width, int Height, string? SourceUrl, bool IsDefault)
{
    public const string BoxArtFileName = "boxart-01.png";
    public const string SnapFileName = "snap-01.png";

    public string FileName => Type == ImageType.BoxArt ? BoxArtFileName : SnapFileName;

    public InfoJsonImage ToInfoJson(DateTimeOffset dateAdded, bool isCover) => new()
    {
        FileName = FileName,
        Type = Type,
        Source = IsDefault ? InfoJsonImage.SourceDefault : InfoJsonImage.SourceLibretro,
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
/// feed the job's <see cref="CircuitBreaker"/>; once it's open nothing more is requested.
/// </summary>
public sealed class MediaService(HttpClient http, IOptions<SourcesOptions> sources, ILogger<MediaService> logger)
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

        var boxArt = await FirstAsync(ImageType.BoxArt, ThumbnailKey.BoxArtFolder).ConfigureAwait(false);
        var snap = await FirstAsync(ImageType.Snap, ThumbnailKey.SnapFolder).ConfigureAwait(false);
        return new MediaResult(boxArt ?? Default(handler), snap);

        async Task<MediaImage?> FirstAsync(ImageType type, string folder)
        {
            foreach (var name in names)
            {
                if (breaker.IsOpen)
                    return null;
                var url = ThumbnailKey.Url(baseUrl, handler.DatName, folder, name);
                if (await GetPngAsync(url, breaker, cancellationToken).ConfigureAwait(false) is { } png)
                    return new MediaImage(type, png.Bytes, png.Info.Width, png.Info.Height, url, IsDefault: false);
            }
            return null;
        }
    }

    public static MediaImage Default(ISystemHandler handler)
    {
        var bytes = handler.DefaultBoxArtPng.ToArray();
        var info = PngInfo.Read(bytes) ?? throw new InvalidOperationException($"Default box art of '{handler.Id}' is not a valid PNG.");
        return new MediaImage(ImageType.BoxArt, bytes, info.Width, info.Height, null, IsDefault: true);
    }

    private async Task<(byte[] Bytes, PngInfo Info)?> GetPngAsync(string url, CircuitBreaker breaker, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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

        if (PngInfo.Read(bytes) is { } info)
            return (bytes, info);
        logger.LogWarning("Thumbnail {Url} is not a valid PNG; ignored", url);
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
