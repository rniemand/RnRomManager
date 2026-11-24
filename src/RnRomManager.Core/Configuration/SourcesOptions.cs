namespace RnRomManager.Core.Configuration;

/// <summary><c>Sources</c> section (spec §4, §10).</summary>
public sealed class SourcesOptions
{
    public const string Section = "Sources";

    public string DatBaseUrl { get; set; } = "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat";

    /// <summary>Spec T2: the blawar/titledb source file for <c>TitleDb</c> systems; required.</summary>
    public string TitleDbUrl { get; set; } = "https://raw.githubusercontent.com/blawar/titledb/master/US.en.json";

    /// <summary>Arcade D1: the MAME <c>-listxml</c> DAT for every <c>Mame</c> system; required.</summary>
    public string MameDatUrl { get; set; } = "https://raw.githubusercontent.com/libretro/mame2003-plus-libretro/master/metadata/mame2003-plus.xml";

    /// <summary>FBNeo D2: the FBNeo arcade-only XML DAT for every <c>Fbneo</c> system; required.</summary>
    public string FbneoDatUrl { get; set; } = "https://raw.githubusercontent.com/libretro/FBNeo/master/dats/FinalBurn%20Neo%20(ClrMame%20Pro%20XML%2C%20Arcade%20only).dat";

    /// <summary>Empty = media fetch disabled (default box art only).</summary>
    public string ThumbnailBaseUrl { get; set; } = "https://thumbnails.libretro.com";

    /// <summary>IGDB suggestions S1: the Twitch app's Client ID; with <see cref="IgdbClientSecret"/>, both set or both empty.</summary>
    public string IgdbClientId { get; set; } = "";

    /// <summary>IGDB suggestions S1, S2: the Twitch app's secret; masked wherever settings are shown or logged.</summary>
    public string IgdbClientSecret { get; set; } = "";

    /// <summary>IGDB suggestions S1: a key counts as set when it is non-empty after trimming.</summary>
    public static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>IGDB suggestions S1: both IGDB keys are set.</summary>
    public static bool IgdbConfigured(SourcesOptions options) => IsSet(options.IgdbClientId) && IsSet(options.IgdbClientSecret);
}
