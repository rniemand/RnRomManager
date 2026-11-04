namespace RnRomManager.Core.Dats;

/// <summary>The 5 DAT files per system (spec §10): no-intro + 4 metadat. The id is both the source folder and the cache folder.</summary>
public static class DatKind
{
    public const string NoIntro = "no-intro";
    public const string ReleaseYear = "releaseyear";
    public const string Developer = "developer";
    public const string Publisher = "publisher";
    public const string Genre = "genre";

    public static IReadOnlyList<string> All { get; } = [NoIntro, ReleaseYear, Developer, Publisher, Genre];

    /// <summary><c>no-intro/Nintendo - Game Boy.dat</c>: relative to <c>{DatBaseUrl}</c> and to <c>cache/dats/</c>.</summary>
    public static string RelativePath(string kind, string datName) => $"{kind}/{datName}.dat";
}
