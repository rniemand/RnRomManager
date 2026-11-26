using RnRomManager.Core.Library;

namespace RnRomManager.Api.Http;

/// <summary>
/// The outcome of <see cref="ImageUrlDownloader.DownloadAsync"/>: the body (at most <see cref="ImageFormats.MaxBytes"/> + 1
/// bytes, so an oversized image is detectable without reading it all), or a problem to return as is.
/// </summary>
public sealed record ImageUrlDownload(
    byte[]? Bytes,
    int Status = StatusCodes.Status200OK,
    string? Code = null,
    string? Title = null);
