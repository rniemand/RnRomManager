namespace RnRomManager.Core.Library;

/// <summary><c>/api/games?sort=</c> (spec §23): tie-break is always <c>id</c>.</summary>
public enum GameSort
{
    Title,
    Year,
    Added,
}
