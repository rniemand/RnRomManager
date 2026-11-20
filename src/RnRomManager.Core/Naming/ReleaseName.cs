using System.Text.RegularExpressions;

namespace RnRomManager.Core.Naming;

/// <summary>A release's name from its file name, for search labels (DLC B10).</summary>
public static partial class ReleaseName
{
    [GeneratedRegex(@"(\s*\[[^\]]*\])+\z", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingTags();

    /// <returns>
    /// The file name without its extension and trailing <c>[…]</c> tags, trimmed; parentheses are kept. The stem when
    /// nothing is left.
    /// </returns>
    public static string Of(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var name = TrailingTags().Replace(stem, "").Trim();
        return name.Length == 0 ? stem : name;
    }
}
