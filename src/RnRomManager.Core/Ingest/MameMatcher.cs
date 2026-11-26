using RnRomManager.Core.Dats;

namespace RnRomManager.Core.Ingest;

public static class MameMatcher
{
    /// <summary>
    /// Required ROMs: not nodump, no <c>merge</c>, no <c>bios</c>. Matched by CRC only (entry names are ignored; extra
    /// entries are allowed). A set with no required ROMs needs one of its dumped ROMs.
    /// </summary>
    public static MameMatch Match(MameSet set, IReadOnlyCollection<MameEntry> entries)
    {
        var crcs = entries.Select(e => e.Crc32).ToHashSet(StringComparer.Ordinal);
        bool Has(MameRom rom) => crcs.Contains(rom.Crc32!);
        var dumped = set.Roms.Where(r => !r.NoDump).ToList();
        var required = dumped.Where(r => r.Merge is null && r.Bios is null).ToList();

        IReadOnlyList<string> missing;
        bool complete;
        if (required.Count > 0)
        {
            missing = required.Where(r => !Has(r)).Select(r => r.Name).ToList();
            complete = missing.Count == 0;
        }
        else
        {
            complete = dumped.Any(Has); // so a set with no dumped ROMs never matches
            missing = complete ? [] : dumped.Select(r => r.Name).ToList();
        }

        var bios = dumped.Where(r => r.Bios is not null).ToList();
        var selfContained = dumped.Where(r => r.Merge is not null && r.Bios is null).All(Has) && (bios.Count == 0 || bios.Any(Has));
        return new MameMatch(complete, missing, selfContained);
    }
}
