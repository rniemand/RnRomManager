namespace RnRomManager.Core.Library;

/// <summary>A validated <c>/api/games</c> query (spec §23, §11, §14.2). Null filters are not applied.</summary>
public sealed record GameQuery
{
    public string? System { get; init; }

    /// <summary><c>A</c>–<c>Z</c> or <c>#</c>: the <c>bucket</c> column.</summary>
    public string? Letter { get; init; }

    /// <summary>Matches a game when any of its releases has this region (<c>region_key LIKE '%|USA|%'</c>).</summary>
    public string? Region { get; init; }

    /// <summary>
    /// Every word of it must appear in <c>display_title</c>, <c>title</c>, a developer, publisher or genre (S1);
    /// <c>%</c>/<c>_</c> match literally.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>null = all, true = only favourites, false = only non-favourites.</summary>
    public bool? Favourite { get; init; }

    /// <summary>Only games in this collection (B15); <see cref="GameSort.Added"/> then sorts by when each was added to it.</summary>
    public Guid? CollectionId { get; init; }

    /// <summary>Only games not in this collection (P1), e.g. the Add games picker.</summary>
    public Guid? NotInCollectionId { get; init; }

    /// <summary>Only these games (a discovery group's page); callers keep the list within <see cref="Database.Page{T}.MaxSize"/>.</summary>
    public IReadOnlyCollection<Guid>? GameIds { get; init; }

    /// <summary>Only games released in or after this year; games without a year are left out (Timeline T9).</summary>
    public int? YearFrom { get; init; }

    /// <summary>Only games released in or before this year; games without a year are left out (Timeline T9).</summary>
    public int? YearTo { get; init; }

    /// <summary>true = only games linked to IGDB (<c>igdb_id &gt; 0</c>, Timeline T2).</summary>
    public bool IgdbLinked { get; init; }

    public GameSort Sort { get; init; } = GameSort.Title;
    public bool Descending { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = Database.Page<GameListItem>.DefaultSize;

    public const int MaxWords = 8;

    /// <summary><see cref="Search"/> split on whitespace, distinct case-insensitively.</summary>
    public IReadOnlyList<string> Words =>
        Search?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];

    /// <summary><see cref="Search"/> trimmed, whitespace runs collapsed to one space: what relevance ranks against.</summary>
    public string? Phrase => Search is null ? null : string.Join(' ', Search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// GS1: when the whole trimmed <see cref="Search"/> is a SHA1 (40 hex) or MD5 (32 hex), its column (<c>sha1</c> or
    /// <c>md5</c>) and the lower-case hash; else null.
    /// </summary>
    public (string Field, string Hash)? HashSearch => Search?.Trim() switch
    {
        { Length: 40 } s when s.All(char.IsAsciiHexDigit) => ("sha1", s.ToLowerInvariant()),
        { Length: 32 } s when s.All(char.IsAsciiHexDigit) => ("md5", s.ToLowerInvariant()),
        _ => null,
    };
}