using System.Globalization;
using System.Text.RegularExpressions;

namespace RnRomManager.Core.Library;

/// <summary>Multi-disc M1: a game's discs that play together, in disc order, and (M2) the name of their playlist.</summary>
public sealed record DiscSet(string Name, IReadOnlyList<RomRow> Discs);

/// <summary>
/// Multi-disc M1–M2 (Saturn/Sega CD S1; Amiga F1–F2: floppy disk sets): finds a game's disc sets from its releases at
/// request time; nothing is stored.
/// </summary>
public static partial class DiscSets
{
    /// <summary>
    /// S1: the most discs a set may have, from each pinned core's playlist reader: PCSX ReARMed's <c>disks[8]</c>,
    /// Yabause's <c>M3U_MAX_FILE</c> (5), Genesis Plus GX's <c>MAX_DISKS</c> (4) and (Amiga F1) PUAE's
    /// <c>DC_MAX_SIZE</c> (20); 0 (no sets) for any other system.
    /// </summary>
    public static int MaxDiscs(string systemId) => systemId switch
    {
        "psx" => 8,
        "saturn" => 5,
        "segacd" => 4,
        AmigaSystemId => 20,
        _ => 0,
    };

    private const string AmigaSystemId = "amiga";

    [GeneratedRegex(@"\(Disk ([0-9]+) of ([0-9]+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiskToken();

    [GeneratedRegex(@"\[[^\]]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketGroup();

    /// <summary>Amiga F1: the <c>n</c> of a file name's one <c>(Disk n of m)</c> token with <c>1 ≤ n ≤ m</c>; null otherwise.</summary>
    public static int? DiskNumber(string fileName) => DiskOf(fileName)?.Number;

    private static (int Number, int Total, Match Token)? DiskOf(string fileName)
    {
        var matches = DiskToken().Matches(fileName);
        if (matches.Count != 1)
            return null;
        var match = matches[0];
        return int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            && number >= 1 && number <= total
                ? (number, total, match)
                : null;
    }

    // S1, CloneCD B10: the main-file extensions a set's discs may have; an .img is a CloneCD disc (or a lone PlayStation image).
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
    public static IReadOnlyList<DiscSet> Find(string systemId, IReadOnlyList<RomRow> roms, ILookup<long, RomFileRow> files)
    {
        if (systemId == AmigaSystemId)
            return FindDisks(roms);
        var sets = new List<DiscSet>();
        var maxDiscs = MaxDiscs(systemId);
        var candidates = roms.Where(r => r.Flags.Count(f => DiscFlag().IsMatch(f)) == 1);
        foreach (var group in candidates.GroupBy(GroupKey))
        {
            var discs = group.OrderBy(r => DiscNumber(r.Flags)).ToList();
            if (discs.Count < 2 || discs.Count > maxDiscs
                || !discs.Select(r => DiscNumber(r.Flags)).SequenceEqual(Enumerable.Range(1, discs.Count).Select(n => (int?)n))
                || !discs.All(r => MainExtensions.Any(e => r.FileName.EndsWith(e, StringComparison.OrdinalIgnoreCase))))
                continue;
            sets.Add(new DiscSet(Name(discs[0], files), discs));
        }
        return sets;
    }

    /// <summary>M2, CloneCD B10: Disc 1's cue or <c>.ccd</c> name (else its file name) without its first " (Disc 1)", as <c>.m3u</c>.</summary>
    public static string Name(RomRow discOne, ILookup<long, RomFileRow> files)
    {
        var cue = files[discOne.Id].FirstOrDefault(f => f.Ordinal == 0
            && (f.FileName.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) || f.FileName.EndsWith(".ccd", StringComparison.OrdinalIgnoreCase)));
        var stem = Path.GetFileNameWithoutExtension(cue?.FileName ?? discOne.FileName);
        return DiscOneToken().Replace(stem, "", 1) + ".m3u";
    }

    /// <summary>
    /// Amiga F1–F2: releases with a disk token group by their total and their file name without the token and every
    /// <c>[…]</c> group; a group is a set when it holds exactly one <c>.adf</c> per disk <c>1..m</c>, 2 ≤ m ≤ 20.
    /// </summary>
    private static List<DiscSet> FindDisks(IReadOnlyList<RomRow> roms)
    {
        var sets = new List<DiscSet>();
        var candidates = roms.Select(r => (Rom: r, Disk: DiskOf(r.FileName))).Where(c => c.Disk is not null)
            .Select(c => (c.Rom, Disk: c.Disk!.Value));
        foreach (var group in candidates.GroupBy(c => (c.Disk.Total, Key: BracketGroup().Replace(WithoutToken(c.Rom.FileName, c.Disk.Token), ""))))
        {
            var total = group.Key.Total;
            var disks = group.OrderBy(c => c.Disk.Number).ToList();
            if (total < 2 || total > MaxDiscs(AmigaSystemId) || disks.Count != total
                || !disks.Select(c => c.Disk.Number).SequenceEqual(Enumerable.Range(1, total))
                || !disks.All(c => c.Rom.FileName.EndsWith(".adf", StringComparison.OrdinalIgnoreCase)))
                continue;
            var one = disks[0];
            sets.Add(new DiscSet(Path.GetFileNameWithoutExtension(WithoutToken(one.Rom.FileName, one.Disk.Token)) + ".m3u",
                disks.Select(c => c.Rom).ToList()));
        }
        return sets;
    }

    private static string WithoutToken(string fileName, Match token) => fileName.Remove(token.Index, token.Length);

    // M1: every identifying field but the disc flag, compared ordinally; '\u001f' can't occur in a field.
    private static string GroupKey(RomRow rom) => string.Join('\u001e',
        rom.BaseTitle,
        string.Join('\u001f', rom.Regions),
        string.Join('\u001f', rom.Languages),
        rom.Revision ?? "\u001f",
        rom.Version ?? "\u001f",
        string.Join('\u001f', rom.Flags.Where(f => !DiscFlag().IsMatch(f))));
}
