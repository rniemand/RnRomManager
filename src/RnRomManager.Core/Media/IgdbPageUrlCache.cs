using System.Collections.Concurrent;

namespace RnRomManager.Core.Media;

/// <summary>
/// T7: IGDB page URLs by game id, per process; found pages and not-found (null) results only, never failures. Cleared
/// whole when it holds <see cref="MaxEntries"/>.
/// </summary>
public sealed class IgdbPageUrlCache
{
    public const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<int, string?> _urls = new();

    public bool TryGet(int igdbId, out string? url) => _urls.TryGetValue(igdbId, out url);

    public void Set(int igdbId, string? url)
    {
        if (_urls.Count >= MaxEntries)
            _urls.Clear();
        _urls[igdbId] = url;
    }
}
