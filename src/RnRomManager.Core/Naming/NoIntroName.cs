using System.Text.RegularExpressions;

namespace RnRomManager.Core.Naming;

/// <summary>
/// A No-Intro name split into base title and tags (spec §6.1 base title, §6.1.1 tag parsing). Base titles come
/// from the DAT game name, tags from the DAT ROM name stem; both use <see cref="Parse"/>.
/// </summary>
public sealed partial record NoIntroName(
    string BaseTitle,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Languages,
    string? Revision,
    IReadOnlyList<string> Flags)
{
    /// <param name="name">A name without file extension, e.g. <c>Tetris (World) (Rev 1)</c>.</param>
    public static NoIntroName Parse(string name)
    {
        // Peel trailing " (…)" groups off the end; what's left is text the groups can't start in.
        var groups = new List<string>();
        var rest = name;
        while (TrailingGroup().Match(rest) is { Success: true } m)
        {
            groups.Insert(0, m.Groups[1].Value);
            rest = rest[..m.Index];
        }

        var regionIndex = groups.FindIndex(NoIntroRegions.IsRegionGroup);
        string baseTitle;
        IEnumerable<string> tagGroups;
        if (regionIndex >= 0)
        {
            // Earlier groups belong to the title: "Yelu Wangzi (Prince Yeh Rude) (Taiwan)".
            baseTitle = rest + string.Concat(groups.Take(regionIndex).Select(g => $" ({g})"));
            tagGroups = groups.Skip(regionIndex);
        }
        else
        {
            var firstParen = name.IndexOf(" (", StringComparison.Ordinal);
            baseTitle = firstParen >= 0 ? name[..firstParen] : name;
            tagGroups = groups;
        }

        List<string> regions = [], languages = [], flags = [];
        string? revision = null;
        var first = true;
        foreach (var group in tagGroups)
        {
            if (first && regionIndex >= 0)
                regions.AddRange(group.Split(", "));
            else if (LanguagesPattern().IsMatch(group))
                languages.AddRange(group.Split(','));
            else if (revision is null && RevisionPattern().IsMatch(group))
                revision = group;
            else
                flags.Add(group);
            first = false;
        }

        return new NoIntroName(baseTitle.Trim(), regions, languages, revision, flags);
    }

    [GeneratedRegex(@" \(([^()]*)\)\s*\z")]
    private static partial Regex TrailingGroup();

    [GeneratedRegex(@"^[A-Z][a-z](,[A-Z][a-z])*\z")]
    private static partial Regex LanguagesPattern();

    [GeneratedRegex(@"^(Rev [0-9A-Z.]+|v\d+(\.\d+)*[a-z]?)\z")]
    private static partial Regex RevisionPattern();
}
