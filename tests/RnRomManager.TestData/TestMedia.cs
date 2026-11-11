using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;

namespace RnRomManager.TestData;

/// <summary>A <see cref="MediaService"/> built by hand for Core tests that don't go through the Api's HTTP clients.</summary>
public static class TestMedia
{
    /// <summary>Media disabled (empty <c>Sources:ThumbnailBaseUrl</c>): every game gets its default box art.</summary>
    public static MediaService Offline() => Create(new HttpClient(), "");

    /// <param name="titleDbImages">The titledb CDN client; a client that is never used when null.</param>
    /// <param name="dats">The DATs titledb names are looked up in; an empty cache when null.</param>
    public static MediaService Create(HttpClient thumbnails, string thumbnailBaseUrl, HttpClient? titleDbImages = null, DatCache? dats = null) =>
        new(thumbnails, new TitleDbImageClient(titleDbImages ?? new HttpClient()),
            dats ?? new DatCache(Options.Create(new PathsOptions()), new SystemRegistry([]), NullLogger<DatCache>.Instance),
            Options.Create(new SourcesOptions { ThumbnailBaseUrl = thumbnailBaseUrl }), NullLogger<MediaService>.Instance);
}
