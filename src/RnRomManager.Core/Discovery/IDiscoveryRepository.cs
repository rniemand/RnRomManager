using RnRomManager.Core.Database;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Discovery;

/// <summary>
/// One discovery group (B2): <see cref="Name"/> is the binary-smallest trimmed spelling, at most
/// <see cref="MaxNameLength"/> characters (ellipsis when cut); <see cref="GameCount"/> counts distinct games.
/// </summary>
public sealed record DiscoveryGroup(string Key, string Name, long GameCount)
{
    public const int MaxNameLength = 255;
}

/// <summary>
/// Live publisher and genre indexes built from <c>games.publishers</c> and <c>games.genres</c> (B2, B3). Each kind's
/// index is computed by MariaDB and kept for up to <see cref="IndexLifetime"/>, so metadata changes show within that.
/// </summary>
public interface IDiscoveryRepository
{
    /// <summary>How long a computed index is reused (the Risks fallback: a live scan misses the 500 ms budget).</summary>
    static readonly TimeSpan IndexLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One page of <paramref name="kind"/>'s groups across every system, sorted by normalized name bytes. A non-blank
    /// <paramref name="q"/> (trimmed) keeps only groups whose <see cref="DiscoveryGroup.Name"/> contains it, ordinal ignore-case,
    /// before paging (UI polish B16: a display filter; grouping and keys are untouched).
    /// </summary>
    Task<Page<DiscoveryGroup>> ListGroupsAsync(DiscoveryKind kind, int page, int pageSize, string? q, CancellationToken cancellationToken);

    /// <summary>The group with <paramref name="key"/>, or null when no game has that value.</summary>
    Task<DiscoveryGroup?> GetGroupAsync(DiscoveryKind kind, string key, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the group's games across systems, sorted by display title, system and id, each game once; null when
    /// the group doesn't exist. Games deleted since the index was computed are left out of their page.
    /// </summary>
    Task<Page<GameListItem>?> ListGamesAsync(DiscoveryKind kind, string key, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>B27.6: forgets every cached index and any in-flight build, so the next read builds from the database.</summary>
    void Invalidate();
}
