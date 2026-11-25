namespace RnRomManager.Core.Library;

/// <summary><c>/api/games?sort=</c> (spec §23): tie-break is always <c>id</c>, after the title where a member says so.</summary>
public enum GameSort
{
    Title,
    /// <summary>Games without a year last in both directions; ties by title, then id (G1).</summary>
    Year,
    Added,

    /// <summary>Unrated last in both directions; ties by title, then id.</summary>
    Rating,

    /// <summary>Exact title match, then prefix, then the rest; ties by title, then id. Ignores the direction.</summary>
    Relevance,
}
