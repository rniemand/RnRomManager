using System.Globalization;

namespace RnRomManager.Core.Library;

/// <summary>Why a list item matched a search when its title didn't (S3): <c>developer</c>, <c>publisher</c> or <c>genre</c>.</summary>
public sealed record SearchMatch(string Field, string Value)
{
    // Close to the database's utf8mb4_unicode_ci: case- and accent-insensitive.
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <returns>
    /// null without words or when any word is in a title; else the first developer, publisher, then genre value that
    /// contains a word.
    /// </returns>
    public static SearchMatch? Find(IReadOnlyList<string> words, string title, string displayTitle,
        IReadOnlyList<string> developers, IReadOnlyList<string> publishers, IReadOnlyList<string> genres)
    {
        if (words.Count == 0 || words.Any(w => Contains(title, w) || Contains(displayTitle, w)))
            return null;
        foreach (var (field, values) in new[] { ("developer", developers), ("publisher", publishers), ("genre", genres) })
            if (values.FirstOrDefault(v => words.Any(w => Contains(v, w))) is { } value)
                return new SearchMatch(field, value);
        return null;
    }

    private static bool Contains(string text, string word) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, word, Options) >= 0;
}
