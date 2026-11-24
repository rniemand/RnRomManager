namespace RnRomManager.Core.Dats;

/// <summary>Spec T1: where a system's DAT comes from. <see cref="Libretro"/> is the zero value, so substitutes stay libretro.</summary>
public enum DatSource
{
    Libretro = 0,
    TitleDb = 1,

    /// <summary>MS-DOS spec N1: no DAT files; nothing is downloaded or loaded, and every file goes to Review.</summary>
    None = 2,

    /// <summary>Arcade D1: one MAME <c>-listxml</c> file from <c>Sources:MameDatUrl</c>; sets are matched by name and ROM CRCs.</summary>
    Mame = 3,

    /// <summary>Arcade (FinalBurn Neo) D1: one FBNeo arcade XML DAT from <c>Sources:FbneoDatUrl</c>; sets are matched by name and ROM CRCs, as <see cref="Mame"/>.</summary>
    Fbneo = 4,
}

public static class DatSources
{
    /// <summary>FBNeo D1: a romset source (<see cref="DatSource.Mame"/> or <see cref="DatSource.Fbneo"/>), matched by set name and ROM CRCs.</summary>
    public static bool IsRomset(this DatSource source) => source is DatSource.Mame or DatSource.Fbneo;
}
