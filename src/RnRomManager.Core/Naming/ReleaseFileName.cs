namespace RnRomManager.Core.Naming;

/// <summary>The tags of a release that release editing (B3) can change and that show in its file name.</summary>
public sealed record ReleaseTags(
    IReadOnlyList<string> Regions, IReadOnlyList<string> Languages, string? Revision, bool Hack, bool Translated, string? Version);

/// <summary>B7: a release's file name after its tags were edited.</summary>
public static class ReleaseFileName
{
    private enum Insert { Default, AfterTitle, End }

    /// <summary>
    /// Edits only the groups of the fields that changed between <paramref name="before"/> and <paramref name="after"/>:
    /// a found group is replaced (or removed with one preceding space), a missing one is inserted before the first
    /// <c>[</c> group (regions right after <paramref name="baseTitle"/>, a version at the very end). The stem is then
    /// sanitized and the extension kept. No change → <paramref name="fileName"/> as it is.
    /// </summary>
    public static string Edit(string fileName, string baseTitle, ReleaseTags before, ReleaseTags after)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var changed = false;
        void Apply(bool differs, string? oldGroup, string? newGroup, Insert insert)
        {
            if (!differs)
                return;
            changed = true;
            stem = Change(stem, baseTitle, oldGroup, newGroup, insert);
        }

        Apply(!before.Regions.SequenceEqual(after.Regions, StringComparer.Ordinal),
            List(before.Regions, ", "), List(after.Regions, ", "), Insert.AfterTitle);
        Apply(!before.Languages.SequenceEqual(after.Languages, StringComparer.Ordinal),
            List(before.Languages, ","), List(after.Languages, ","), Insert.Default);
        Apply(!string.Equals(before.Revision ?? "", after.Revision ?? "", StringComparison.Ordinal),
            Paren(before.Revision), Paren(after.Revision), Insert.Default);
        Apply(before.Translated != after.Translated,
            before.Translated ? "(Translated)" : null, after.Translated ? "(Translated)" : null, Insert.Default);
        Apply(before.Hack != after.Hack, before.Hack ? "(Hack)" : null, after.Hack ? "(Hack)" : null, Insert.Default);
        Apply(!string.Equals(before.Version ?? "", after.Version ?? "", StringComparison.Ordinal),
            string.IsNullOrEmpty(before.Version) ? null : $"[{before.Version}]",
            string.IsNullOrEmpty(after.Version) ? null : $"[{after.Version}]", Insert.End);

        return changed ? FileNameSanitizer.Sanitize(stem) + Path.GetExtension(fileName) : fileName;
    }

    private static string? List(IReadOnlyList<string> values, string separator) =>
        values.Count == 0 ? null : $"({string.Join(separator, values)})";

    private static string? Paren(string? value) => string.IsNullOrEmpty(value) ? null : $"({value})";

    private static string Change(string stem, string baseTitle, string? oldGroup, string? newGroup, Insert insert)
    {
        var at = oldGroup is null ? -1 : stem.IndexOf(oldGroup, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            if (newGroup is not null)
                return stem[..at] + newGroup + stem[(at + oldGroup!.Length)..];
            var start = at > 0 && stem[at - 1] == ' ' ? at - 1 : at;
            return stem[..start] + stem[(at + oldGroup!.Length)..];
        }
        if (newGroup is null)
            return stem;
        if (insert == Insert.End)
            return stem + newGroup;
        if (insert == Insert.AfterTitle && baseTitle.Length > 0 && stem.StartsWith(baseTitle, StringComparison.OrdinalIgnoreCase))
            return stem[..baseTitle.Length] + " " + newGroup + stem[baseTitle.Length..];
        var bracket = stem.IndexOf('[', StringComparison.Ordinal);
        if (bracket < 0)
            return stem + " " + newGroup;
        // Before the space that leads the first [ group, so "Orbit [id]" becomes "Orbit (Hack) [id]".
        var before = bracket > 0 && stem[bracket - 1] == ' ' ? bracket - 1 : bracket;
        return stem[..before] + " " + newGroup + stem[before..];
    }
}
