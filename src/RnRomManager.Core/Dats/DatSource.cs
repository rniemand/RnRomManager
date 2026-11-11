namespace RnRomManager.Core.Dats;

/// <summary>Spec T1: where a system's DAT comes from. <see cref="Libretro"/> is the zero value, so substitutes stay libretro.</summary>
public enum DatSource
{
    Libretro = 0,
    TitleDb = 1,
}
