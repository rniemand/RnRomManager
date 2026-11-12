using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Dats;

/// <summary>T4: what a Switch title id is. An application delta is classified as DLC.</summary>
public enum TitleKind
{
    Base,
    Update,
    Dlc,
}

/// <summary>
/// T4/T5: a ROM matched through titledb. <see cref="Base"/> is the base game's entry (its <see cref="GameName"/> is the
/// release's <c>datGameName</c>); the release name, languages and file name come from <paramref name="NameEntry"/>
/// when it has one, else from the base.
/// </summary>
/// <param name="TitleId">The id the file carries, kept in its file name.</param>
/// <param name="NameEntry">The entry whose name the release uses; null when it falls back to the base name.</param>
/// <param name="BaseTitle">T5 grouping key and game title.</param>
/// <param name="RomFileName">T5 rom <c>fileName</c>.</param>
/// <param name="Revision"><c>v&lt;N&gt;</c> when N &gt; 0.</param>
public sealed record TitleDbMatch(string TitleId, TitleKind Kind, TitleDbRow Base, TitleDbRow? NameEntry, string BaseTitle, string GameName,
    string RomFileName, string? Revision, IReadOnlyList<string> Flags, IReadOnlyList<string> Languages);

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
    public static string RelativePath(string datName) => $"{Folder}/{datName}.json";

    /// <summary>The compact files' folder below <c>cache/dats/</c>.</summary>
    public const string Folder = "titledb";

    /// <summary>
    /// T5's rom <c>fileName</c>: the cleaned name (dot runs → <c>…</c>, since <c>..</c> isn't allowed), sanitized, then
    /// <c>[&lt;TitleId&gt;][v&lt;N&gt;]</c>, B6's <c>[&lt;releaseVersion&gt;]</c> when given, and the lower-case extension.
    /// </summary>
    public static string RomFileName(string name, string titleId, uint version, string extension, string? releaseVersion = null) =>
        $"{FileNameSanitizer.Sanitize(DotRun().Replace(Normalize(name), "…"))} [{titleId}][v{version}]{(releaseVersion is null ? "" : $"[{releaseVersion}]")}{extension.ToLowerInvariant()}";

    /// <summary>T5's <c>N</c>: the first case-sensitive <c>[v&lt;digits&gt;]</c> in the file name; none or overflow → 0.</summary>
    public static uint VersionFrom(string fileName) =>
        VersionTag().Match(fileName) is { Success: true } tag
        && uint.TryParse(tag.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : 0;

    /// <summary>
    /// T4: the header's <c>titleId</c> when it has one (a non-Switch id there means no match, whatever the file name
    /// says), else the first <c>[0100…]</c> in the file name; upper-cased. Null: no title id. B7: a base id from the
    /// file name of a file marked <c>[UPD]</c> (ignoring case) becomes its update id (<c>base | 0x800</c>).
    /// </summary>
    public static string? TitleIdFrom(IReadOnlyDictionary<string, object?>? headerFields, string fileName)
    {
        if (headerFields is not null && headerFields.TryGetValue("titleId", out var header) && header?.ToString() is { } fromHeader)
            return IsSwitchTitleId(fromHeader) ? fromHeader.ToUpperInvariant() : null;
        if (FileNameTitleId().Match(fileName) is not { Success: true } match)
            return null;
        var id = ulong.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);
        return (id & 0x1FFFUL) == 0 && fileName.Contains("[UPD]", StringComparison.OrdinalIgnoreCase) ? Hex(id | 0x800) : Hex(id);
    }

    /// <summary>
    /// T4/T5: classifies <paramref name="titleId"/> by its low 13 bits and names the release. Base game: its own entry
    /// must exist. Update (<c>base | 0x800</c>): the base must exist. DLC (<c>base + 0x1001</c> … <c>+ 0x17D0</c>), its
    /// data patch (that + 0x800, named from the DLC it patches) and an application delta (<c>base + 0xC00</c>): the
    /// base must exist. Anything else, or a missing base, is no match.
    /// </summary>
    /// <param name="fileName">The ingest file name: its <c>[vN]</c> and extension.</param>
    /// <param name="byTitleId">The store's lookup by upper-case id.</param>
    public static TitleDbMatch? Match(string titleId, string fileName, Func<string, TitleDbRow?> byTitleId)
    {
        if (!IsSwitchTitleId(titleId))
            return null;
        titleId = titleId.ToUpperInvariant();
        var id = ulong.Parse(titleId, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);
        var baseId = id & ~0x1FFFUL;
        if (byTitleId(Hex(baseId)) is not { } baseEntry)
            return null;

        var low = id - baseId;
        (TitleKind Kind, TitleDbRow? Named, string Name)? classified = low switch
        {
            0 => (TitleKind.Base, baseEntry, baseEntry.Name),
            0x800 => byTitleId(titleId) is { } update ? (TitleKind.Update, update, update.Name) : (TitleKind.Update, null, baseEntry.Name),
            0xC00 => (TitleKind.Dlc, null, baseEntry.Name + " (DLC)"),
            >= 0x1001 and <= 0x17D0 => Dlc(byTitleId(titleId)),
            >= 0x1801 and <= 0x1FD0 => Dlc(byTitleId(Hex(id - 0x800))), // a data patch: named from the DLC it patches
            _ => null,
        };
        if (classified is not var (kind, named, name))
            return null;

        var version = VersionFrom(fileName);
        var languages = (named ?? baseEntry).Languages
            .Select(code => code.Length == 0 ? code : char.ToUpperInvariant(code[0]) + code[1..])
            .Distinct(StringComparer.Ordinal).ToList();
        return new TitleDbMatch(titleId, kind, baseEntry, named, BaseTitle(baseEntry.Name), GameName(baseEntry.Name, baseEntry.TitleId),
            RomFileName(name, titleId, version, Path.GetExtension(fileName), kind == TitleKind.Update ? ReleaseVersion.From(fileName) : null), version > 0 ? $"v{version}" : null,
            kind switch { TitleKind.Update => ["Update"], TitleKind.Dlc => ["DLC"], _ => [] }, languages);

        (TitleKind, TitleDbRow?, string) Dlc(TitleDbRow? own) => (TitleKind.Dlc, own, own?.Name ?? baseEntry.Name + " (DLC)");
    }

    /// <summary>T7: a new game's metadata from the base entry (developers only when titledb has one).</summary>
    public static DatMetadata MetadataOf(TitleDbRow entry) => new(entry.ReleaseYear,
        entry.Developer is null ? [] : [entry.Developer], entry.Publisher is null ? [] : [entry.Publisher], entry.Genres);

    /// <summary>The title id a T3 <c>GameName</c> ends with (<c>… [0100…]</c>); null when it has none.</summary>
    public static string? TitleIdOfGameName(string gameName) =>
        GameNameTitleId().Match(gameName) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>B8: a base id's update id (<c>| 0x800</c>), an update id as is; null for any other kind.</summary>
    public static string? UpdateIdOf(string titleId)
    {
        var id = ulong.Parse(titleId, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);
        return (id & 0x1FFFUL) is 0 or 0x800 ? Hex(id | 0x800) : null;
    }

    private static string Hex(ulong id) => id.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);

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

    [GeneratedRegex(@" \[(0100[0-9A-F]{12})\]\z")]
    private static partial Regex GameNameTitleId();

    [GeneratedRegex(@"\[(0100[0-9A-Fa-f]{12})\]")]
    private static partial Regex FileNameTitleId();

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
