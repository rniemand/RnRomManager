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
}
