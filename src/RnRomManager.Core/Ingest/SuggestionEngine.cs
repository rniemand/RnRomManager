using System.Text.RegularExpressions;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Ingest;

/// <summary>Review suggestions (§10.3, D45): one best release per distinct base title.</summary>
public static class SuggestionEngine
{
    public static IReadOnlyList<Suggestion> Find(DatStore? store, string? headerTitle, string originalFileName, long sizeBytes)
    {
        if (store is null)
            return [];
        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        var cut = new[] { stem.IndexOf(" (", StringComparison.Ordinal), stem.IndexOf(" [", StringComparison.Ordinal) }
            .Where(i => i >= 0).DefaultIfEmpty(stem.Length).Min();
        var inputs = new[] { Normalize(headerTitle), Normalize(stem[..cut]) }.Where(s => s.Length > 0).ToArray();
        if (inputs.Length == 0)
            return [];
        return store.Roms.Select(rom =>
            {
                var title = NoIntroName.Parse(rom.GameName).BaseTitle;
                var normalized = Normalize(title);
                var score = inputs.Max(input => Similarity(input, normalized)) + (rom.SizeBytes == sizeBytes ? 0.1 : 0);
                return (Title: title, Suggestion: new Suggestion(rom.GameName, rom.RomName, score));
            })
            .GroupBy(x => x.Title, StringComparer.Ordinal)
            .Select(g => g.Select(x => x.Suggestion).OrderByDescending(s => s.Score).ThenBy(s => s.RomName, StringComparer.Ordinal).First())
            .Where(s => s.Score >= 0.4)
            .OrderByDescending(s => s.Score).ThenBy(s => s.RomName, StringComparer.Ordinal)
            .Take(5).ToList();
    }

    public static string Normalize(string? value) => Regex.Replace(
        Regex.Replace((value ?? "").ToUpperInvariant(), "[^A-Z0-9 ]", ""), " +", " ").Trim();

    private static double Similarity(string input, string title)
    {
        var a = input.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var b = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var union = a.Union(b).Count();
        var jaccard = union == 0 ? 0 : (double)a.Intersect(b).Count() / union;
        var left = input.Replace(" ", "", StringComparison.Ordinal);
        var right = title.Replace(" ", "", StringComparison.Ordinal);
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        var maxLength = Math.Max(left.Length, right.Length);
        var edit = maxLength == 0 ? 0 : 1 - (double)previous[right.Length] / maxLength;
        return Math.Max(jaccard, edit);
    }
}
