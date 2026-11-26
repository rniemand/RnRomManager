namespace RnRomManager.Core.Discovery;

/// <summary>
/// One discovery group (B2): <see cref="Name"/> is the binary-smallest trimmed spelling, at most
/// <see cref="MaxNameLength"/> characters (ellipsis when cut); <see cref="GameCount"/> counts distinct games.
/// </summary>
public sealed record DiscoveryGroup(string Key, string Name, long GameCount)
{
    public const int MaxNameLength = 255;
}
