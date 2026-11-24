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
/// L4: one IGDB game for <b>Lookup Info</b>, already mapped to the Edit Metadata form's rules. <see cref="CoverUrl"/> is the
/// list thumbnail (t_cover_small); <see cref="CoverImageUrl"/> is the same cover at the size Suggest Images stores (t_1080p).
/// </summary>
public sealed record GameInfoMatch(int IgdbId, string Name, int? ReleaseYear, IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers, IReadOnlyList<string> Genres, string? Description, string? CoverUrl, string? CoverImageUrl, string? Url);

/// <summary>The matches, or the failure title (the image suggestions' titles) when IGDB couldn't be used.</summary>
public sealed record GameInfoLookupResult(IReadOnlyList<GameInfoMatch>? Matches, string? FailedTitle);

/// <summary>IGDB metadata lookup (L3): up to five games matching a title, for the Edit Metadata modal.</summary>
public interface IGameInfoLookup
{
    /// <summary>Both <c>Sources:Igdb*</c> keys are set.</summary>
    bool Configured { get; }

    /// <summary><paramref name="title"/> goes through <see cref="IgdbClient.SearchTitle"/>. Cancellation propagates.</summary>
    Task<GameInfoLookupResult> LookupAsync(string title, string systemId, CancellationToken cancellationToken);
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
    : IImageSuggestions, IGameInfoLookup
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

    /// <summary>L3: the lookup's fields.</summary>
    public const string LookupFields =
        "name,url,summary,first_release_date,release_dates.y,release_dates.platform,genres.name,involved_companies.developer," +
        "involved_companies.publisher,involved_companies.company.name,cover.image_id";

    private const int MaxListItems = 20;
    private const int MaxListItemLength = 255;
    private const int MaxDescriptionLength = 4000;

    private sealed class IgdbFailure(string title) : Exception(title);

    private sealed record IgdbImage(string ImageId, int? Width, int? Height);

    private sealed record IgdbGame(string Name, IgdbImage? Cover, IReadOnlyList<IgdbImage> Screenshots);

    private SourcesOptions Sources => options.Value;

    public bool Configured => SourcesOptions.IgdbConfigured(Sources);

    public async Task<ImageSuggestionResult> SuggestAsync(string title, string systemId, ImageType type, CancellationToken cancellationToken)
    {
        if (type is not (ImageType.BoxArt or ImageType.Snap))
            throw new ArgumentOutOfRangeException(nameof(type), type, "Only BoxArt and Snap are suggested.");
        var (games, failed) = await SearchOnPlatformAsync("suggestions", title, systemId, Fields, false,
            (root, _) => ParseGames(root), cancellationToken).ConfigureAwait(false);
        return failed is null ? ImageSuggestionResult.Ok(Build(games!, type)) : ImageSuggestionResult.Failed(failed);
    }

    public async Task<GameInfoLookupResult> LookupAsync(string title, string systemId, CancellationToken cancellationToken)
    {
        var (matches, failed) = await SearchOnPlatformAsync("lookup", title, systemId, LookupFields, true, ParseMatches, cancellationToken)
            .ConfigureAwait(false);
        return new GameInfoLookupResult(matches, failed);
    }

    /// <summary>
    /// S4/L3: searches the system's IGDB platform, then once more on every platform when that finds nothing; failures become
    /// their S8 title. <paramref name="parse"/> gets the response array and the system's platform.
    /// </summary>
    private async Task<(List<T>? Items, string? FailedTitle)> SearchOnPlatformAsync<T>(string what, string title, string systemId,
        string fields, bool excludeVersions, Func<JsonElement, int?, List<T>> parse, CancellationToken cancellationToken)
    {
        if (!Configured)
            throw new InvalidOperationException("IGDB isn't configured.");
        try
        {
            var search = SearchTitle(title);
            int? platform = IgdbPlatforms.ById.TryGetValue(systemId, out var id) ? id : null;
            var items = parse(await SearchAsync(SearchBody(search, platform, fields, excludeVersions), cancellationToken).ConfigureAwait(false), platform);
            if (items.Count == 0 && platform is not null)
                items = parse(await SearchAsync(SearchBody(search, null, fields, excludeVersions), cancellationToken).ConfigureAwait(false), platform);
            return (items, null);
        }
        catch (IgdbFailure failure)
        {
            logger.LogWarning("IGDB {What} for {Title} failed: {Reason}", what, title, failure.Message);
            return (null, failure.Message);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // S3a: connection errors and the shared policy's timeout (Core can't name Polly's exception).
            logger.LogWarning("IGDB {What} for {Title} failed: {Type}: {Message}", what, title, ex.GetType().Name, ex.Message);
            return (null, Unreachable);
        }
    }

    /// <summary>S4: the display title without <c>"</c> or <c>\</c>, so it can't break out of the Apicalypse string.</summary>
    public static string SearchTitle(string title) => title.Replace("\"", "", StringComparison.Ordinal).Replace("\\", "", StringComparison.Ordinal).Trim();

    /// <summary>S4: the Apicalypse body; <paramref name="platform"/> null searches every platform.</summary>
    public static string SearchBody(string title, int? platform) => SearchBody(title, platform, Fields, false);

    /// <summary>L3: <paramref name="excludeVersions"/> adds <c>version_parent = null</c>, dropping editions such as "Gold Edition".</summary>
    public static string SearchBody(string title, int? platform, string fields, bool excludeVersions)
    {
        var conditions = new List<string>();
        if (excludeVersions)
            conditions.Add("version_parent = null");
        if (platform is { } p)
            conditions.Add($"platforms = ({p.ToString(CultureInfo.InvariantCulture)})");
        var where = conditions.Count > 0 ? $" where {string.Join(" & ", conditions)};" : "";
        return $"search \"{title}\"; fields {fields};{where} limit {MaxSuggestions};";
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

    /// <summary>One <c>/v4/games</c> POST (with the S3 401 retry): the response's root array, cloned.</summary>
    private async Task<JsonElement> SearchAsync(string searchBody, CancellationToken cancellationToken)
    {
        var token = await tokens.GetAsync(FetchTokenAsync, cancellationToken).ConfigureAwait(false);
        var (status, body) = await PostSearchAsync(token, searchBody, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.Unauthorized)
        {
            tokens.Invalidate(token);
            token = await tokens.GetAsync(FetchTokenAsync, cancellationToken).ConfigureAwait(false);
            (status, body) = await PostSearchAsync(token, searchBody, cancellationToken).ConfigureAwait(false);
            if (status == HttpStatusCode.Unauthorized)
                throw new IgdbFailure(Refused);
        }
        ThrowOnStatus(status);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                throw new IgdbFailure(Unreadable);
            return json.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new IgdbFailure(Unreadable);
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> PostSearchAsync(string token, string searchBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GamesUrl)
        {
            Content = new StringContent(searchBody, Encoding.UTF8, "text/plain"),
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

    private static List<IgdbGame> ParseGames(JsonElement root)
    {
        var games = new List<IgdbGame>();
        foreach (var game in root.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object)
                continue;
            var name = Text(game, "name") ?? "";
            var cover = game.TryGetProperty("cover", out var c) ? Image(c) : null;
            var screenshots = game.TryGetProperty("screenshots", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(Image).OfType<IgdbImage>().ToList()
                : [];
            games.Add(new IgdbGame(name, cover, screenshots));
        }
        return games;
    }

    /// <summary>L4: each IGDB game with a positive <c>id</c> and a name, mapped to the form's rules.</summary>
    private static List<GameInfoMatch> ParseMatches(JsonElement root, int? platform)
    {
        var matches = new List<GameInfoMatch>();
        foreach (var game in root.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object || Dimension(game, "id") is not { } id || id <= 0
                || Text(game, "name")?.Trim() is not { Length: > 0 } name)
                continue;
            var companies = Objects(game, "involved_companies").ToList();
            var summary = Text(game, "summary")?.Trim();
            var imageId = game.TryGetProperty("cover", out var cover) ? Image(cover)?.ImageId : null;
            var url = Text(game, "url");
            matches.Add(new GameInfoMatch(id, name, ReleaseYear(game, platform),
                Names(companies.Where(c => IsTrue(c, "developer")).Select(CompanyName)),
                Names(companies.Where(c => IsTrue(c, "publisher")).Select(CompanyName)),
                Names(Objects(game, "genres").Select(g => Text(g, "name"))),
                string.IsNullOrEmpty(summary) ? null : summary.Length > MaxDescriptionLength ? summary[..MaxDescriptionLength] : summary,
                imageId is null ? null : $"{ImageBaseUrl}t_cover_small/{imageId}.jpg",
                imageId is null ? null : $"{ImageBaseUrl}t_1080p/{imageId}.jpg",
                url is not null && url.StartsWith("https://www.igdb.com/", StringComparison.Ordinal) ? url : null));
        }
        return matches;
    }

    /// <summary>L4: the earliest release year on the system's platform, else the year of <c>first_release_date</c>; 1970–2100 only.</summary>
    private static int? ReleaseYear(JsonElement game, int? platform)
    {
        var year = platform is null ? null
            : Objects(game, "release_dates").Where(d => Dimension(d, "platform") == platform).Select(d => Dimension(d, "y")).Min();
        if (year is null && game.TryGetProperty("first_release_date", out var date) && date.ValueKind == JsonValueKind.Number
            && date.TryGetInt64(out var seconds) && seconds is >= -62135596800 and <= 253402300799)
            year = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.Year;
        return year is >= 1970 and <= 2100 ? year : null;
    }

    private static string? CompanyName(JsonElement involved) =>
        involved.TryGetProperty("company", out var company) && company.ValueKind == JsonValueKind.Object ? Text(company, "name") : null;

    /// <summary>L4: trimmed, empty and over-long names dropped, distinct (ordinal), in IGDB order, at most 20.</summary>
    private static List<string> Names(IEnumerable<string?> names) =>
        names.Select(n => n?.Trim()).OfType<string>().Where(n => n.Length is > 0 and <= MaxListItemLength)
            .Distinct(StringComparer.Ordinal).Take(MaxListItems).ToList();

    private static IEnumerable<JsonElement> Objects(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

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
