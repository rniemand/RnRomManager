namespace RnRomManager.Systems.Commodore;

/// <summary>V3: the image type a gzipped VICE disk image holds, for the C64, PET and VIC-20 handlers' <c>SourceExtension</c>.</summary>
internal static class ViceImages
{
    // libretro-vice treats every one of these as a floppy, and VICE probes the image type (so a gzipped .d80 stored as .d81 still attaches).
    private static readonly Dictionary<string, string> Gzipped = new(StringComparer.Ordinal)
    {
        [".d6z"] = ".d64", [".d7z"] = ".d71", [".d8z"] = ".d81", [".g6z"] = ".g64", [".x6z"] = ".x64", [".g4z"] = ".g41",
    };

    /// <summary>
    /// <c>.d6z</c> and its siblings map to the image they hold; <c>Game.d64.gz</c> to <c>.d64</c> when that inner type is one of
    /// <paramref name="romExtensions"/> (not <c>.gz</c> or a gzipped type), else null; any other extension to itself, lower-cased.
    /// </summary>
    public static string? SourceExtension(string fileName, IReadOnlyList<string> romExtensions)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (Gzipped.TryGetValue(extension, out var image))
            return image;
        if (extension != ".gz")
            return extension;
        var inner = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName)).ToLowerInvariant();
        return inner.Length > 0 && inner != ".gz" && !Gzipped.ContainsKey(inner) && romExtensions.Contains(inner) ? inner : null;
    }
}
