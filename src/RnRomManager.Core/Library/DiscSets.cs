using System.Globalization;
using System.Text.RegularExpressions;

namespace RnRomManager.Core.Library;

/// <summary>Multi-disc M1: a game's discs that play together, in disc order, and (M2) the name of their playlist.</summary>
public sealed record DiscSet(string Name, IReadOnlyList<RomRow> Discs);

/// <summary>Multi-disc M1–M2: finds a PlayStation game's disc sets from its releases at request time; nothing is stored.</summary>
public static partial class DiscSets
{
    /// <summary>M1: PCSX ReARMed's libretro disk table (<c>disks[8]</c>) ignores further playlist lines.</summary>
    public const int MaxDiscs = 8;

    private static readonly string[] MainExtensions = [".bin", ".iso", ".chd", ".img"];

    [GeneratedRegex(@"^Disc ([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscFlag();

    [GeneratedRegex(@" \(Disc 1\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscOneToken();

    /// <summary>M1: the number of the first <c>Disc &lt;n&gt;</c> flag (case ignored, <c>Disc 01</c> is 1); null without one.</summary>
    public static int? DiscNumber(IReadOnlyList<string> flags) =>
        flags.Select(f => DiscFlag().Match(f)).FirstOrDefault(m => m.Success) is { } match
        && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>M1: the sets among <paramref name="roms"/> (one game's releases), each ordered by disc; <paramref name="files"/> gives a cue set's cue (ordinal 0) for M2.</summary>
    public static IReadOnlyList<DiscSet> Find(IReadOnlyList<RomRow> roms, ILookup<long, RomFileRow> files)
    {
        var sets = new List<DiscSet>();
        var candidates = roms.Where(r => r.Flags.Count(f => DiscFlag().IsMatch(f)) == 1);
        foreach (var group in candidates.GroupBy(GroupKey))
        {
            var discs = group.OrderBy(r => DiscNumber(r.Flags)).ToList();
            if (discs.Count is < 2 or > MaxDiscs
                || !discs.Select(r => DiscNumber(r.Flags)).SequenceEqual(Enumerable.Range(1, discs.Count).Select(n => (int?)n))
                || !discs.All(r => MainExtensions.Any(e => r.FileName.EndsWith(e, StringComparison.OrdinalIgnoreCase))))
                continue;
            sets.Add(new DiscSet(Name(discs[0], files), discs));
        }
        return sets;
    }

    /// <summary>M2: Disc 1's cue name (else its file name) without its first " (Disc 1)", as <c>.m3u</c>.</summary>
    public static string Name(RomRow discOne, ILookup<long, RomFileRow> files)
    {
        var cue = files[discOne.Id].FirstOrDefault(f => f.Ordinal == 0 && f.FileName.EndsWith(".cue", StringComparison.OrdinalIgnoreCase));
        var stem = Path.GetFileNameWithoutExtension(cue?.FileName ?? discOne.FileName);
        return DiscOneToken().Replace(stem, "", 1) + ".m3u";
    }

    // M1: every identifying field but the disc flag, compared ordinally; '\u001f' can't occur in a field.
    private static string GroupKey(RomRow rom) => string.Join('\u001e',
        rom.BaseTitle,
        string.Join('\u001f', rom.Regions),
        string.Join('\u001f', rom.Languages),
        rom.Revision ?? "\u001f",
        rom.Version ?? "\u001f",
        string.Join('\u001f', rom.Flags.Where(f => !DiscFlag().IsMatch(f))));
}
