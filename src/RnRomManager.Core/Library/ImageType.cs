namespace RnRomManager.Core.Library;

/// <summary>spec §6.2; the MVP fetches <see cref="BoxArt"/> and <see cref="Snap"/> only.</summary>
public enum ImageType
{
    BoxArt,
    Snap,
    TitleScreen,
    Other,
}
