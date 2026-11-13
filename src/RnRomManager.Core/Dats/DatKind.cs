namespace RnRomManager.Core.Dats;

/// <summary>
/// The DAT files per system (spec §10): a main DAT (<c>no-intro</c> or <c>redump</c>, the handler's <c>DatFolder</c>, B1)
/// plus the metadat files the handler lists (<c>MetadatKinds</c>, B2). The id is both the source folder and the cache folder.
/// </summary>
public static class DatKind
{
    public const string NoIntro = "no-intro";
    public const string ReleaseYear = "releaseyear";
    public const string Developer = "developer";
    public const string Publisher = "publisher";
    public const string Genre = "genre";
    public const string Redump = "redump";

    public static IReadOnlyList<string> All { get; } = [NoIntro, ReleaseYear, Developer, Publisher, Genre];

    /// <summary>The four metadat kinds, in <see cref="All"/> order: the default <c>MetadatKinds</c>.</summary>
    public static IReadOnlyList<string> Metadat { get; } = [ReleaseYear, Developer, Publisher, Genre];

    /// <summary>True for a manifest path in a main-DAT folder (<c>no-intro/</c> or <c>redump/</c>), which carries the version.</summary>
    public static bool IsMainDat(string file) =>
        file.StartsWith(NoIntro + "/", StringComparison.Ordinal) || file.StartsWith(Redump + "/", StringComparison.Ordinal);

    /// <summary><c>no-intro/Nintendo - Game Boy.dat</c>: relative to <c>{DatBaseUrl}</c> and to <c>cache/dats/</c>.</summary>
    public static string RelativePath(string kind, string datName) => $"{kind}/{datName}.dat";
}
