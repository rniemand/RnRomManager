namespace RnRomManager.Core.Discovery;

/// <summary>Which discovery list a shared logo belongs to (B13); stored lowercase in <c>discovery_logos.kind</c>.</summary>
public enum LogoKind
{
    Publishers,
    Genres,
    Systems,

    /// <summary>C2: developer groups, keyed as publishers and genres.</summary>
    Developers,
}
