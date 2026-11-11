using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Dats;

/// <summary>One kept titledb entry, as the compact file stores it (spec T2). <see cref="Name"/> is titledb's own name.</summary>
public sealed record TitleDbRow(string TitleId, string Name, string? Publisher, string? Developer, int? ReleaseYear,
    IReadOnlyList<string> Genres, IReadOnlyList<string> Languages, string? BoxArtUrl, string? SnapUrl);

/// <summary>
/// Spec T2, T3, T5: the blawar/titledb source file (<c>US.en.json</c>), the compact per-system file made from it, and
/// the pure naming and URL rules the compact rows are validated with.
/// </summary>
public static partial class TitleDb
{
    public const string CdnHost = "img-eshop.cdn.nintendo.net";

    /// <summary><c>images.source_url</c>.</summary>
    public const int MaxUrlLength = 1024;

    /// <summary>T3: the most a compact file may hold.</summary>
    public const long MaxCompactBytes = 256L * 1024 * 1024;

    /// <summary><c>roms.dat_game_name</c>, in Unicode scalar values.</summary>
    public const int MaxGameNameLength = 255;

    private static readonly JsonSerializerOptions Upstream = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true, // every property must be present, nullable ones included
    };

    /// <summary>A Switch title id in any letter case (T2, T4). <c>\z</c>, so a trailing line break doesn't pass.</summary>
    public static bool IsSwitchTitleId(string? id) => id is not null && SwitchId().IsMatch(id);

    /// <summary>T8's CDN rule: absolute HTTPS on exactly <see cref="CdnHost"/>, port 443, no user info, ≤ 1024 characters.</summary>
    public static bool IsCdnUrl(string? url) =>
        url is { Length: > 0 and <= MaxUrlLength }
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.HostNameType == UriHostNameType.Dns
        && string.Equals(uri.Host, CdnHost, StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443
        && uri.UserInfo.Length == 0;

    /// <summary>
    /// T5's base title: <c>™®©</c> removed, <c>": "</c> → <c>" - "</c>, whitespace runs → one space, and a leading
    /// <c>The</c>/<c>A</c>/<c>An</c> moved to the end of the first <c>" - "</c> segment.
    /// </summary>
    public static string BaseTitle(string name)
    {
        var title = Normalize(name);
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        var (first, rest) = dash < 0 ? (title, "") : (title[..dash], title[dash..]);
        return LeadingArticle().Match(first) is { Success: true } article
            ? $"{article.Groups[2].Value}, {article.Groups[1].Value}{rest}"
            : title;
    }

    /// <summary>T3's unique store name: <c>&lt;base title&gt; [&lt;TitleId&gt;]</c>.</summary>
    public static string GameName(string name, string titleId) => $"{BaseTitle(name)} [{titleId}]";

    /// <summary>The compact file's path below <c>cache/dats/</c> (T2).</summary>
    public static string RelativePath(string datName) => $"titledb/{datName}.json";

    /// <summary>
    /// T5's rom <c>fileName</c>: the cleaned name (dot runs → <c>…</c>, since <c>..</c> isn't allowed), sanitized, then
    /// <c>[&lt;TitleId&gt;][v&lt;N&gt;]</c> and the lower-case extension.
    /// </summary>
    public static string RomFileName(string name, string titleId, uint version, string extension) =>
        $"{FileNameSanitizer.Sanitize(DotRun().Replace(Normalize(name), "…"))} [{titleId}][v{version}]{extension.ToLowerInvariant()}";

    /// <summary>T5's <c>N</c>: the first case-sensitive <c>[v&lt;digits&gt;]</c> in the file name; none or overflow → 0.</summary>
    public static uint VersionFrom(string fileName) =>
        VersionTag().Match(fileName) is { Success: true } tag
        && uint.TryParse(tag.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : 0;

    /// <summary>T2: a name the library can use: its title sanitizes to something and its <see cref="GameName"/> fits.</summary>
    public static bool IsUsableName(string? name, string titleId) =>
        !string.IsNullOrWhiteSpace(name)
        && FileNameSanitizer.Sanitize(BaseTitle(name)).Length > 0
        && GameName(name, titleId).EnumerateRunes().Count() <= MaxGameNameLength;

    /// <summary>
    /// T2: reads the upstream file (a JSON object keyed by nsuId) as a stream and keeps the usable Switch entries, the
    /// first one of a repeated id, in document order.
    /// </summary>
    /// <exception cref="JsonException">Not a JSON object of entries (JSON null included).</exception>
    public static async Task<IReadOnlyList<TitleDbRow>> ParseUpstreamAsync(Stream source, CancellationToken ct)
    {
        var entries = await JsonSerializer.DeserializeAsync<OrderedDictionary<string, UpstreamEntry?>>(source, Upstream, ct).ConfigureAwait(false)
            ?? throw new JsonException("The titledb file is JSON null, not an object.");
        var rows = new List<TitleDbRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.Values)
        {
            if (entry is null || !IsSwitchTitleId(entry.Id) || !IsUsableName(entry.Name, entry.Id!.ToUpperInvariant()))
                continue;
            var id = entry.Id.ToUpperInvariant();
            if (!seen.Add(id))
                continue;
            rows.Add(new TitleDbRow(id, entry.Name!, entry.Publisher, entry.Developer, Year(entry.ReleaseDate),
                Members(entry.Category), Members(entry.Languages).Distinct(StringComparer.Ordinal).ToList(),
                Url(entry.IconUrl ?? entry.FrontBoxArt), Url(entry.Screenshots?.FirstOrDefault())));
        }
        return rows;
    }

    /// <summary>Writes the compact file: a JSON array of rows, camelCase, nulls written.</summary>
    public static Task WriteCompactAsync(Stream target, IReadOnlyList<TitleDbRow> rows, CancellationToken ct) =>
        JsonSerializer.SerializeAsync(target, rows, Compact, ct);

    /// <summary>Reads and checks a compact file (T2): any row that T2 wouldn't have written fails the whole file.</summary>
    /// <exception cref="JsonException">Malformed, or a row breaks a rule.</exception>
    public static IReadOnlyList<TitleDbRow> ReadCompact(Stream source)
    {
        var rows = JsonSerializer.Deserialize<List<TitleDbRow?>>(source, Compact)
            ?? throw new JsonException("The compact titledb file is JSON null, not an array.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            if (Invalid(rows[i]) is { } problem)
                throw new JsonException($"Compact titledb row {i}: {problem}.");
            if (!seen.Add(rows[i]!.TitleId))
                throw new JsonException($"Compact titledb row {i}: title id {rows[i]!.TitleId} repeats.");
        }
        return rows!;
    }

    private static string? Invalid(TitleDbRow? row) => row switch
    {
        null => "null",
        _ when !CanonicalId().IsMatch(row.TitleId ?? "") => $"title id '{row.TitleId}' isn't canonical",
        _ when !IsUsableName(row.Name, row.TitleId!) => $"name '{row.Name}' is unusable",
        { ReleaseYear: < 1000 or > 9999 } => $"release year {row.ReleaseYear} is out of range",
        _ when row.Genres is null || row.Genres.Any(string.IsNullOrWhiteSpace) => "genres are null or hold a blank member",
        _ when row.Languages is null || row.Languages.Any(string.IsNullOrWhiteSpace) => "languages are null or hold a blank member",
        _ when (row.BoxArtUrl is not null && !IsCdnUrl(row.BoxArtUrl)) || (row.SnapUrl is not null && !IsCdnUrl(row.SnapUrl)) => "an image URL isn't a CDN URL",
        _ => null,
    };

    /// <summary><c>yyyymmdd</c> → its year; a bare plausible year as is; anything else null (fits <c>SMALLINT</c>).</summary>
    private static int? Year(long? date) => date switch
    {
        >= 10000101 and <= 99991231 => (int)(date / 10000),
        >= 1970 and <= 2100 => (int)date,
        _ => null,
    };

    private static List<string> Members(IReadOnlyList<string?>? values) =>
        values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList() ?? [];

    private static string? Url(string? url) => IsCdnUrl(url) ? url : null;

    private static string Normalize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is not ('™' or '®' or '©'))
                builder.Append(c);
        }
        var collapsed = Whitespace().Replace(builder.ToString(), " ");
        return Whitespace().Replace(collapsed.Replace(": ", " - ", StringComparison.Ordinal), " ").Trim();
    }

    [GeneratedRegex(@"\A0100[0-9A-Fa-f]{12}\z")]
    private static partial Regex SwitchId();

    [GeneratedRegex(@"\A0100[0-9A-F]{12}\z")]
    private static partial Regex CanonicalId();

    [GeneratedRegex(@"\.{2,}")]
    private static partial Regex DotRun();

    [GeneratedRegex(@"\[v([0-9]+)\]")]
    private static partial Regex VersionTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\A(The|A|An) (.+)\z", RegexOptions.Singleline)]
    private static partial Regex LeadingArticle();

    /// <summary>Only the upstream fields used; unknown ones (<c>size</c>, <c>version</c>, …) are ignored.</summary>
    private sealed record UpstreamEntry(string? Id, string? Name, string? Publisher, string? Developer,
        long? ReleaseDate,
        IReadOnlyList<string?>? Category, IReadOnlyList<string?>? Languages, string? IconUrl, string? FrontBoxArt,
        IReadOnlyList<string?>? Screenshots);
}
