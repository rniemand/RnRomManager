using System.Data;
using System.Text;
using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Library;

namespace RnRomManager.Data.Repositories;

/// <summary>
/// Discovery indexes over <c>games</c> (B2, B3). Normalizing every entry and grouping them in MariaDB takes over a
/// second at 50,000 games, so each kind's index is computed in one pass and reused for
/// <see cref="IDiscoveryRepository.IndexLifetime"/> (Risks fallback). Register as a singleton.
/// </summary>
public sealed class DiscoveryRepository(IDbConnectionFactory connections, IGameRepository games, TimeProvider time)
    : IDiscoveryRepository
{
    private sealed record EntryRow(string Key, byte[] Norm, byte[] Trimmed, Guid GameId);

    private sealed record Group(DiscoveryGroup Info, byte[] Norm, Guid[] GameIds);

    private sealed class Index(List<Group> groups)
    {
        public List<Group> Groups { get; } = groups;
        public Dictionary<string, Group> ByKey { get; } = groups.ToDictionary(g => g.Info.Key, StringComparer.Ordinal);
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<DiscoveryKind, (Task<Index> Build, DateTimeOffset StartedAt)> _indexes = [];

    public async Task<Page<DiscoveryGroup>> ListGroupsAsync(
        DiscoveryKind kind, int page, int pageSize, string? q, DiscoveryGroupSort sort, bool descending, CancellationToken cancellationToken)
    {
        var groups = (await IndexAsync(kind, cancellationToken)).Groups.Select(g => g.Info).ToList();
        if (!string.IsNullOrWhiteSpace(q))
            groups = groups.FindAll(g => g.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase));
        // A1: the index is in normalized-name order; LINQ's sort is stable, so Games ties keep it (name ascending).
        if (sort == DiscoveryGroupSort.Games)
            groups = (descending ? groups.OrderByDescending(g => g.GameCount) : groups.OrderBy(g => g.GameCount)).ToList();
        else if (descending)
            groups.Reverse();
        var items = groups.Skip(Page<object>.Offset(page, pageSize)).Take(pageSize).ToList();
        return new Page<DiscoveryGroup>(items, page, pageSize, groups.Count);
    }

    public async Task<DiscoveryGroup?> GetGroupAsync(DiscoveryKind kind, string key, CancellationToken cancellationToken) =>
        (await IndexAsync(kind, cancellationToken)).ByKey.GetValueOrDefault(key)?.Info;

    public async Task<string?> FindKeyAsync(DiscoveryKind kind, string name, CancellationToken cancellationToken)
    {
        string key;
        await using (var connection = connections.Create())
            key = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                $"SELECT SHA2({DiscoverySql.Norm(DiscoverySql.Trimmed("@name"))}, 256)", new { name }, cancellationToken: cancellationToken)) ?? "";
        return (await IndexAsync(kind, cancellationToken)).ByKey.ContainsKey(key) ? key : null;
    }

    public async Task<Page<GameListItem>?> ListGamesAsync(
        DiscoveryKind kind, string key, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (!(await IndexAsync(kind, cancellationToken)).ByKey.TryGetValue(key, out var group))
            return null;
        var slice = group.GameIds.Skip(Page<object>.Offset(page, pageSize)).Take(pageSize).ToArray();
        if (slice.Length == 0)
            return new Page<GameListItem>([], page, pageSize, group.GameIds.Length);

        // The library list's own projection, for just this page's games, put back in the index's order.
        var rows = await games.ListAsync(new GameQuery { GameIds = slice, PageSize = slice.Length }, cancellationToken);
        var byId = rows.Items.ToDictionary(g => g.Id);
        var items = slice.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        return new Page<GameListItem>(items, page, pageSize, group.GameIds.Length);
    }

    public void Invalidate()
    {
        // A build already running finishes for its own waiters, but its result is no longer kept for anyone else.
        lock (_lock)
            _indexes.Clear();
    }

    /// <summary>The kind's index: the cached one while fresh, else one shared rebuild (a failed build isn't kept).</summary>
    private Task<Index> IndexAsync(DiscoveryKind kind, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            if (!_indexes.TryGetValue(kind, out var cached)
                || cached.Build.IsFaulted || cached.Build.IsCanceled
                || (cached.Build.IsCompleted && now - cached.StartedAt >= IDiscoveryRepository.IndexLifetime))
            {
                // Not the caller's token: other callers share this build.
                cached = (Task.Run(() => BuildAsync(kind)), now);
                _indexes[kind] = cached;
            }

            // WaitAsync returns an already-finished build even for a cancelled token.
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<Index>(cancellationToken)
                : cached.Build.WaitAsync(cancellationToken);
        }
    }

    private async Task<Index> BuildAsync(DiscoveryKind kind)
    {
        List<EntryRow> entries;
        Dictionary<Guid, int> rank;
        await using (var connection = connections.Create())
        {
            await connection.OpenAsync();
            // One snapshot for both reads, so every entry's game has its rank.
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            entries = (await connection.QueryAsync<EntryRow>(new CommandDefinition($"""
                WITH {DiscoverySql.ValuesCte(kind)}
                SELECT {DiscoverySql.Key} AS `Key`, norm AS Norm, CAST(trimmed AS BINARY) AS Trimmed, game_id AS GameId
                FROM discovery_values
                """, transaction: transaction, commandTimeout: 300))).AsList();
            var order = await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM games ORDER BY display_title, system_id, id", transaction: transaction, commandTimeout: 300));
            rank = order.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
            await transaction.CommitAsync();
        }

        var groups = entries.GroupBy(e => e.Key, StringComparer.Ordinal).Select(g =>
        {
            // Binary comparisons of MariaDB's bytes, as MIN() and ORDER BY on them would do.
            var label = g.Select(e => e.Trimmed).Min(ByteOrder.Instance)!;
            var ids = g.Select(e => e.GameId).Distinct().OrderBy(id => rank.GetValueOrDefault(id, int.MaxValue)).ThenBy(id => id).ToArray();
            return new Group(new DiscoveryGroup(g.Key, DisplayName(label), ids.Length), g.First().Norm, ids);
        }).ToList();
        groups.Sort((a, b) => ByteOrder.Instance.Compare(a.Norm, b.Norm));
        return new Index(groups);
    }

    /// <summary>The UTF-8 label, cut to <see cref="DiscoveryGroup.MaxNameLength"/> characters (code points) with an ellipsis.</summary>
    internal static string DisplayName(byte[] utf8)
    {
        var name = Encoding.UTF8.GetString(utf8);
        var runes = name.EnumerateRunes().ToList();
        return runes.Count <= DiscoveryGroup.MaxNameLength
            ? name
            : string.Concat(runes.Take(DiscoveryGroup.MaxNameLength - 1).Select(r => r.ToString())) + "…";
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
