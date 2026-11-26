namespace RnRomManager.Api.Endpoints;

/// <summary>The <c>?dir=asc|desc</c> query parameter (case-insensitive).</summary>
public static class SortQuery
{
    /// <returns>True for descending; <paramref name="whenMissing"/> when <paramref name="dir"/> is null; null (with a <c>dir</c> error) when invalid.</returns>
    public static bool? ParseDir(string? dir, Dictionary<string, string[]> errors, bool? whenMissing)
    {
        switch (dir?.ToLowerInvariant())
        {
            case null:
                return whenMissing;
            case "asc":
                return false;
            case "desc":
                return true;
            default:
                errors["dir"] = ["Must be asc or desc."];
                return null;
        }
    }
}
