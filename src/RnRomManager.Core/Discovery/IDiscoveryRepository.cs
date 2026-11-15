using RnRomManager.Core.Database;

namespace RnRomManager.Core.Discovery;

/// <summary>
/// One discovery group (B2): <see cref="Name"/> is the binary-smallest trimmed spelling, at most
/// <see cref="MaxNameLength"/> characters (ellipsis when cut); <see cref="GameCount"/> counts distinct games.
/// </summary>
public sealed record DiscoveryGroup(string Key, string Name, long GameCount)
{
    public const int MaxNameLength = 255;
}

/// <summary>Live publisher and genre indexes built from <c>games.publishers</c> and <c>games.genres</c> (B2, B3).</summary>
public interface IDiscoveryRepository
{
    /// <summary>One page of <paramref name="kind"/>'s groups across every system, sorted by normalized name bytes.</summary>
    Task<Page<DiscoveryGroup>> ListGroupsAsync(DiscoveryKind kind, int page, int pageSize, CancellationToken cancellationToken);
}
