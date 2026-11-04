namespace RnRomManager.Core.Media;

/// <summary>libretro-thumbnails file names (spec §10.2).</summary>
public static class ThumbnailKey
{
    private const string Replaced = "&*/:`<>?\\|\"";

    public const string BoxArtFolder = "Named_Boxarts";
    public const string SnapFolder = "Named_Snaps";

    /// <summary>The DAT <c>game.name</c> with each of the 11 characters replaced by <c>_</c>, then URL-encoded as one path segment.</summary>
    public static string For(string datGameName) =>
        Uri.EscapeDataString(string.Concat(datGameName.Select(c => Replaced.Contains(c) ? '_' : c)));

    /// <summary><c>{base}/{DatName}/{folder}/{key}.png</c>.</summary>
    public static string Url(string baseUrl, string datName, string folder, string datGameName) =>
        $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(datName)}/{folder}/{For(datGameName)}.png";
}
