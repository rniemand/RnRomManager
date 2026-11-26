namespace RnRomManager.Core.Reports;

public static class MissingImageReasons
{
    /// <summary>No image has <c>is_cover = 1</c>. Wins when both apply (only possible after DB drift).</summary>
    public const string NoCover = "NoCover";

    /// <summary><c>has_default_boxart = 1</c>: the cover is the placeholder art.</summary>
    public const string Placeholder = "Placeholder";
}
