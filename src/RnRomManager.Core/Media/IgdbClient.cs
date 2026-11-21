using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Media;

/// <summary>IGDB suggestions S6: one candidate image. <see cref="Width"/>/<see cref="Height"/> are the stored (t_1080p) size.</summary>
public sealed record ImageSuggestion(string PreviewUrl, string ImageUrl, string GameName, int? Width, int? Height);

/// <summary>The suggestions, or the S8 failure title when IGDB couldn't be used.</summary>
public sealed record ImageSuggestionResult(IReadOnlyList<ImageSuggestion>? Suggestions, string? FailedTitle)
{
    public static ImageSuggestionResult Ok(IReadOnlyList<ImageSuggestion> suggestions) => new(suggestions, null);

    public static ImageSuggestionResult Failed(string title) => new(null, title);
}

/// <summary>IGDB suggestions: candidate box art and screenshots for a game (S3–S6).</summary>
public interface IImageSuggestions
{
    /// <summary>S1: both <c>Sources:Igdb*</c> keys are set.</summary>
    bool Configured { get; }

    /// <summary><paramref name="type"/> is <see cref="ImageType.BoxArt"/> or <see cref="ImageType.Snap"/>. Cancellation propagates (S3a).</summary>
    Task<ImageSuggestionResult> SuggestAsync(string title, string systemId, ImageType type, CancellationToken cancellationToken);
}

/// <summary>
/// S3: the Twitch app access token, shared by every (transient) <see cref="IgdbClient"/>. Cached until <c>expires_in</c>
/// minus an hour (minus half the lifetime for a token that lives an hour or less); one token request at a time.
/// </summary>
public sealed class IgdbTokenCache(TimeProvider time)
{
    private sealed record Entry(string Token, DateTimeOffset Expires);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile Entry? _entry;

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
            _entry = new Entry(value, time.GetUtcNow().AddSeconds(expiresIn - margin));
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

/// <summary>
/// IGDB suggestions S3–S7: Twitch client-credentials token, then an IGDB <c>/v4/games</c> search on the game's platform
/// (and once more without it when nothing matches). Registered by the Api as a typed client with the shared retry policy
/// and no redirects.
/// </summary>
public sealed partial class IgdbClient(HttpClient http, IgdbTokenCache tokens, IOptions<SourcesOptions> options, ILogger<IgdbClient> logger)
    : IImageSuggestions
{
    public const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    public const string GamesUrl = "https://api.igdb.com/v4/games";
    public const string ImageBaseUrl = "https://images.igdb.com/igdb/image/upload/";
    public const int MaxSuggestions = 5;

    public const string Unreachable = "IGDB couldn't be reached.";
    public const string Refused = "IGDB refused the Client ID or secret.";
    public const string Busy = "IGDB is busy; try again in a moment.";
    public const string Unreadable = "IGDB returned an unreadable response.";

    private const string Fields =
        "name,cover.image_id,cover.width,cover.height,screenshots.image_id,screenshots.width,screenshots.height";

    private sealed class IgdbFailure(string title) : Exception(title);

    private sealed record IgdbImage(string ImageId, int? Width, int? Height);

    private sealed record IgdbGame(string Name, IgdbImage? Cover, IReadOnlyList<IgdbImage> Screenshots);

    private SourcesOptions Sources => options.Value;

    public bool Configured => SourcesOptions.IgdbConfigured(Sources);

    public async Task<ImageSuggestionResult> SuggestAsync(string title, string systemId, ImageType type, CancellationToken cancellationToken)
    {
        if (type is not (ImageType.BoxArt or ImageType.Snap))
            throw new ArgumentOutOfRangeException(nameof(type), type, "Only BoxArt and Snap are suggested.");
        if (!Configured)
            throw new InvalidOperationException("IGDB isn't configured.");
        try
        {
            var search = SearchTitle(title);
            int? platform = IgdbPlatforms.ById.TryGetValue(systemId, out var id) ? id : null;
            var games = await SearchAsync(search, platform, cancellationToken).ConfigureAwait(false);
            if (games.Count == 0 && platform is not null)
                games = await SearchAsync(search, null, cancellationToken).ConfigureAwait(false);
            return ImageSuggestionResult.Ok(Build(games, type));
        }
        catch (IgdbFailure failure)
        {
            logger.LogWarning("IGDB suggestions for {Title} failed: {Reason}", title, failure.Message);
            return ImageSuggestionResult.Failed(failure.Message);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // S3a: connection errors and the shared policy's timeout (Core can't name Polly's exception).
            logger.LogWarning("IGDB suggestions for {Title} failed: {Type}: {Message}", title, ex.GetType().Name, ex.Message);
            return ImageSuggestionResult.Failed(Unreachable);
        }
    }

    /// <summary>S4: the display title without <c>"</c> or <c>\</c>, so it can't break out of the Apicalypse string.</summary>
    public static string SearchTitle(string title) => title.Replace("\"", "", StringComparison.Ordinal).Replace("\\", "", StringComparison.Ordinal).Trim();

    /// <summary>S4: the Apicalypse body; <paramref name="platform"/> null searches every platform.</summary>
    public static string SearchBody(string title, int? platform)
    {
        var where = platform is { } p ? $" where platforms = ({p.ToString(CultureInfo.InvariantCulture)});" : "";
        return $"search \"{title}\"; fields {Fields};{where} limit {MaxSuggestions};";
    }

    /// <summary>S6: <c>t_1080p</c> fits the original into 1920×1080 and never scales up.</summary>
    public static (int? Width, int? Height) StoredSize(int? width, int? height)
    {
        if (width is not > 0 || height is not > 0)
            return (null, null);
        var scale = Math.Min(1.0, Math.Min(1920.0 / width.Value, 1080.0 / height.Value));
        return ((int)Math.Round(width.Value * scale, MidpointRounding.AwayFromZero), (int)Math.Round(height.Value * scale, MidpointRounding.AwayFromZero));
    }

    private static List<ImageSuggestion> Build(IReadOnlyList<IgdbGame> games, ImageType type)
    {
        if (type == ImageType.BoxArt)
            return games.Where(g => g.Cover is not null).Select(g => Suggestion(g, g.Cover!, "t_cover_big")).Take(MaxSuggestions).ToList();
        var first = games.FirstOrDefault(g => g.Screenshots.Count > 0);
        return first is null ? [] : first.Screenshots.Take(MaxSuggestions).Select(s => Suggestion(first, s, "t_screenshot_med")).ToList();
    }

    private static ImageSuggestion Suggestion(IgdbGame game, IgdbImage image, string previewSize)
    {
        var (width, height) = StoredSize(image.Width, image.Height);
        return new ImageSuggestion($"{ImageBaseUrl}{previewSize}/{image.ImageId}.jpg", $"{ImageBaseUrl}t_1080p/{image.ImageId}.jpg",
            game.Name, width, height);
    }

    private async Task<IReadOnlyList<IgdbGame>> SearchAsync(string title, int? platform, CancellationToken cancellationToken)
    {
        var token = await tokens.GetAsync(FetchTokenAsync, cancellationToken).ConfigureAwait(false);
        var (status, body) = await PostSearchAsync(token, title, platform, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.Unauthorized)
        {
            tokens.Invalidate(token);
            token = await tokens.GetAsync(FetchTokenAsync, cancellationToken).ConfigureAwait(false);
            (status, body) = await PostSearchAsync(token, title, platform, cancellationToken).ConfigureAwait(false);
            if (status == HttpStatusCode.Unauthorized)
                throw new IgdbFailure(Refused);
        }
        ThrowOnStatus(status);
        return ParseGames(body);
    }

    private async Task<(HttpStatusCode Status, string Body)> PostSearchAsync(string token, string title, int? platform, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GamesUrl)
        {
            Content = new StringContent(SearchBody(title, platform), Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add("Client-ID", Sources.IgdbClientId.Trim());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>S3: credentials go in the form body only; <c>RequestLoggingHandler</c> logs URLs.</summary>
    private async Task<(string Token, int ExpiresIn)> FetchTokenAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Sources.IgdbClientId.Trim(),
                ["client_secret"] = Sources.IgdbClientSecret.Trim(),
                ["grant_type"] = "client_credentials",
            }),
        };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new IgdbFailure(Refused);
        ThrowOnStatus(response.StatusCode);
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("access_token", out var accessToken) && accessToken.ValueKind == JsonValueKind.String
                && accessToken.GetString() is { Length: > 0 } value
                && root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number
                && expires.TryGetInt32(out var expiresIn) && expiresIn > 0)
                return (value, expiresIn);
        }
        catch (JsonException)
        {
        }
        throw new IgdbFailure(Unreadable);
    }

    private static void ThrowOnStatus(HttpStatusCode status)
    {
        if (status == HttpStatusCode.TooManyRequests)
            throw new IgdbFailure(Busy);
        if ((int)status is < 200 or > 299)
            throw new IgdbFailure($"IGDB returned HTTP {(int)status}.");
    }

    private static List<IgdbGame> ParseGames(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                throw new IgdbFailure(Unreadable);
            var games = new List<IgdbGame>();
            foreach (var game in json.RootElement.EnumerateArray())
            {
                if (game.ValueKind != JsonValueKind.Object)
                    continue;
                var name = game.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                var cover = game.TryGetProperty("cover", out var c) ? Image(c) : null;
                var screenshots = game.TryGetProperty("screenshots", out var s) && s.ValueKind == JsonValueKind.Array
                    ? s.EnumerateArray().Select(Image).OfType<IgdbImage>().ToList()
                    : [];
                games.Add(new IgdbGame(name, cover, screenshots));
            }
            return games;
        }
        catch (JsonException)
        {
            throw new IgdbFailure(Unreadable);
        }
    }

    /// <summary>S6: an image whose <c>image_id</c> isn't plain alphanumerics is skipped.</summary>
    private static IgdbImage? Image(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("image_id", out var id) || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { } imageId || !ImageIdPattern().IsMatch(imageId))
            return null;
        return new IgdbImage(imageId, Dimension(element, "width"), Dimension(element, "height"));
    }

    private static int? Dimension(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex ImageIdPattern();
}
