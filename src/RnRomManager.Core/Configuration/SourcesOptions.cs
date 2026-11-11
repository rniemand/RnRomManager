namespace RnRomManager.Core.Configuration;

/// <summary><c>Sources</c> section (spec §4, §10).</summary>
public sealed class SourcesOptions
{
    public const string Section = "Sources";

    public string DatBaseUrl { get; set; } = "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat";

    /// <summary>Spec T2: the blawar/titledb source file for <c>TitleDb</c> systems; required.</summary>
    public string TitleDbUrl { get; set; } = "https://raw.githubusercontent.com/blawar/titledb/master/US.en.json";

    /// <summary>Empty = media fetch disabled (default box art only).</summary>
    public string ThumbnailBaseUrl { get; set; } = "https://thumbnails.libretro.com";
}
