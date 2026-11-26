namespace RnRomManager.Core.Discovery;

/// <summary>Which game metadata array a discovery index groups by (B2).</summary>
public enum DiscoveryKind
{
    Publishers,
    Genres,

    /// <summary>Timeline polish C2: <c>games.developers</c>.</summary>
    Developers,
}
