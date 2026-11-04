using System.Globalization;
using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Dats;

/// <summary>
/// One system's DATs in memory (spec §10.1): SHA1 → ROM, CRC → merged metadat, and the base-title list for
/// search and suggestions. Immutable; a refresh builds a new one.
/// </summary>
public sealed class DatStore
{
    private readonly Dictionary<string, DatRom> _bySha1 = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DatMetadata> _metadata;

    public DatStore(NoIntroDat noIntro, IEnumerable<MetadatFile> metadat)
    {
        DatName = noIntro.DatName;
        Version = noIntro.Version;
        Roms = noIntro.Roms;
        foreach (var rom in noIntro.Roms)
            _bySha1.TryAdd(rom.Sha1, rom); // the DATs have no duplicate SHA1s; first wins if one appears
        BaseTitles = noIntro.Roms.Select(r => NoIntroName.Parse(r.GameName).BaseTitle).Distinct(StringComparer.Ordinal).ToList();
        _metadata = MergeMetadata(noIntro.Roms.Select(r => r.Crc32).ToHashSet(StringComparer.Ordinal), metadat);
    }

    /// <summary>no-intro header <c>name</c>, e.g. <c>Nintendo - Game Boy</c>.</summary>
    public string DatName { get; }

    /// <summary>no-intro header <c>version</c> (D25); metadat files have none.</summary>
    public string? Version { get; }

    public IReadOnlyList<DatRom> Roms { get; }

    /// <summary>Distinct base titles (§6.1) in DAT order.</summary>
    public IReadOnlyList<string> BaseTitles { get; }

    /// <param name="sha1">Lowercase hex.</param>
    public DatRom? BySha1(string sha1) => _bySha1.GetValueOrDefault(sha1);

    /// <param name="crc32">Lowercase hex.</param>
    public DatMetadata MetadataFor(string crc32) => _metadata.GetValueOrDefault(crc32) ?? DatMetadata.Empty;

    /// <summary>
    /// Blocks sharing a CRC are merged distinct in file order; <c>releaseyear</c> = the first that parses; CRCs
    /// with no no-intro ROM are ignored.
    /// </summary>
    private static Dictionary<string, DatMetadata> MergeMetadata(HashSet<string> knownCrcs, IEnumerable<MetadatFile> files)
    {
        var years = new Dictionary<string, int>(StringComparer.Ordinal);
        var lists = new Dictionary<(string Field, string Crc), List<string>>();
        foreach (var file in files)
        {
            foreach (var value in file.Values.Where(v => knownCrcs.Contains(v.Crc32)))
            {
                if (file.Field == DatKind.ReleaseYear)
                {
                    if (int.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
                        years.TryAdd(value.Crc32, year);
                    continue;
                }
                if (!lists.TryGetValue((file.Field, value.Crc32), out var list))
                    lists[(file.Field, value.Crc32)] = list = [];
                if (!list.Contains(value.Value, StringComparer.Ordinal))
                    list.Add(value.Value);
            }
        }

        IReadOnlyList<string> Get(string field, string crc) => lists.GetValueOrDefault((field, crc)) ?? (IReadOnlyList<string>)[];
        return years.Keys.Concat(lists.Keys.Select(k => k.Crc)).Distinct(StringComparer.Ordinal).ToDictionary(
            crc => crc,
            crc => new DatMetadata(
                years.TryGetValue(crc, out var y) ? y : null,
                Get(DatKind.Developer, crc),
                Get(DatKind.Publisher, crc),
                Get(DatKind.Genre, crc)),
            StringComparer.Ordinal);
    }
}
