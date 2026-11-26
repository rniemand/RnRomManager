namespace RnRomManager.Core.Media;

/// <summary>
/// S3: the Twitch app access token, shared by every (transient) <see cref="IgdbClient"/>. Cached until <c>expires_in</c>
/// minus an hour (minus half the lifetime for a token that lives an hour or less); one token request at a time.
/// </summary>
public sealed class IgdbTokenCache(TimeProvider time)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IgdbTokenCacheEntry? _entry;

    public async Task<string> GetAsync(Func<CancellationToken, Task<(string Token, int ExpiresIn)>> fetch, CancellationToken cancellationToken)
    {
        if (Current() is { } token)
            return token;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current() is { } fresh)
                return fresh;
            var (value, expiresIn) = await fetch(cancellationToken).ConfigureAwait(false);
            var margin = Math.Min(3600, expiresIn / 2);
            _entry = new IgdbTokenCacheEntry(value, time.GetUtcNow().AddSeconds(expiresIn - margin));
            return value;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>S3: IGDB refused <paramref name="token"/>; the next <see cref="GetAsync"/> fetches a new one.</summary>
    public void Invalidate(string token)
    {
        var entry = _entry;
        if (entry?.Token == token)
            _entry = null;
    }

    private string? Current() => _entry is { } entry && time.GetUtcNow() < entry.Expires ? entry.Token : null;
}
