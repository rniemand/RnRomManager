namespace RnRomManager.Core.Dats;

public static class DatSources
{
    /// <summary>FBNeo D1: a romset source (<see cref="DatSource.Mame"/> or <see cref="DatSource.Fbneo"/>), matched by set name and ROM CRCs.</summary>
    public static bool IsRomset(this DatSource source) => source is DatSource.Mame or DatSource.Fbneo;
}
