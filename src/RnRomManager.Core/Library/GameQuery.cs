namespace RnRomManager.Core.Library;

/// <summary>A validated <c>/api/games</c> query (spec §23, §11, §14.2). Null filters are not applied.</summary>
public sealed record GameQuery
{
    public string? System { get; init; }

    /// <summary><c>A</c>–<c>Z</c> or <c>#</c>: the <c>bucket</c> column.</summary>
    public string? Letter { get; init; }

    /// <summary>Matches a game when any of its releases has this region (<c>region_key LIKE '%|USA|%'</c>).</summary>
    public string? Region { get; init; }

    /// <summary>Substring of <c>display_title</c> or <c>title</c>; <c>%</c>/<c>_</c> match literally.</summary>
    public string? Search { get; init; }

    public GameSort Sort { get; init; } = GameSort.Title;
    public bool Descending { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = Database.Page<GameListItem>.DefaultSize;
}
