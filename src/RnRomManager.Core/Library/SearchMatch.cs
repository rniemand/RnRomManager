using System.Globalization;

namespace RnRomManager.Core.Library;

/// <summary>A DLC or update's name (<c>Field</c> <c>dlc</c> or <c>update</c>), matched by <see cref="SearchMatch.Find"/>.</summary>
public sealed record AddOnName(string Field, string Name);

/// <summary>
/// Why a list item matched a search when its title didn't (S3, DLC B10): <c>developer</c>, <c>publisher</c>, <c>genre</c>,
/// <c>dlc</c> or <c>update</c>.
/// </summary>
public sealed record SearchMatch(string Field, string Value)
{
    // Close to the database's utf8mb4_unicode_ci: case- and accent-insensitive.
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <param name="addOns">The game's DLC and update names, in the order they are tried (DLC first).</param>
    /// <returns>
    /// null without words or when any word is in a title; else the first developer, publisher, genre, then add-on value
    /// that contains a word.
    /// </returns>
    public static SearchMatch? Find(IReadOnlyList<string> words, string title, string displayTitle,
        IReadOnlyList<string> developers, IReadOnlyList<string> publishers, IReadOnlyList<string> genres,
        IReadOnlyList<AddOnName> addOns)
    {
        if (words.Count == 0 || words.Any(w => Contains(title, w) || Contains(displayTitle, w)))
            return null;
        foreach (var (field, values) in new[] { ("developer", developers), ("publisher", publishers), ("genre", genres) })
            if (values.FirstOrDefault(v => words.Any(w => Contains(v, w))) is { } value)
                return new SearchMatch(field, value);
        return addOns.FirstOrDefault(a => words.Any(w => Contains(a.Name, w))) is { } addOn
            ? new SearchMatch(addOn.Field, addOn.Name)
            : null;
    }

    private static bool Contains(string text, string word) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, word, Options) >= 0;
}
