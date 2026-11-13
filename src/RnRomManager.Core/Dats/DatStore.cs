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
    private readonly Dictionary<string, TitleDbRow> _byTitleId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DatRom>> _bySerialCode = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <see cref="DatName"/> of every titledb store (T3).</summary>
    public const string TitleDbName = "titledb";

    /// <param name="romExtensions">
    /// N3: the system's ROM extensions (e.g. <c>.nes</c>); DAT ROMs with any other extension (case-insensitive) are left
    /// out of every index, since no file the system accepts could match them. Null keeps every ROM.
    /// </param>
    public DatStore(NoIntroDat noIntro, IEnumerable<MetadatFile> metadat, IReadOnlyCollection<string>? romExtensions = null)
    {
        DatName = noIntro.DatName;
        Version = noIntro.Version;
        Roms = romExtensions is null
            ? noIntro.Roms
            : noIntro.Roms.Where(r => romExtensions.Contains(Path.GetExtension(r.RomName), StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var rom in Roms)
            _bySha1.TryAdd(rom.Sha1, rom); // Redump DATs repeat a game per serial with the same rom (B3); the first wins
        IndexSerials();
        BaseTitles = Roms.Select(r => NoIntroName.Parse(r.GameName).BaseTitle).Distinct(StringComparer.Ordinal).ToList();
        _metadata = MergeMetadata(Roms.Select(r => r.Crc32).ToHashSet(StringComparer.Ordinal), metadat);
    }

    /// <summary>
    /// T3: a titledb store. One ROM per entry: <c>GameName</c> <c>&lt;base title&gt; [&lt;TitleId&gt;]</c> (unique, as
    /// the ids are), the T5 file name with <c>v0</c> and <c>.nsp</c>, size 0 and empty hashes, so nothing is in the
    /// SHA1 index; entries are found by <see cref="ByTitleId"/>.
    /// </summary>
    /// <param name="version">The manifest's <c>yyyy.MM.dd</c>.</param>
    public DatStore(IReadOnlyList<TitleDbRow> rows, string version)
    {
        DatName = TitleDbName;
        Version = version;
        Roms = rows.Select(r => new DatRom(TitleDb.GameName(r.Name, r.TitleId), TitleDb.RomFileName(r.Name, r.TitleId, 0, ".nsp"), 0, "", "", "")).ToList();
        foreach (var row in rows)
            _byTitleId.Add(row.TitleId, row); // the compact reader already refused repeats
        BaseTitles = Roms.Select(r => NoIntroName.Parse(r.GameName).BaseTitle).Distinct(StringComparer.Ordinal).ToList();
        _metadata = [];
    }

    /// <summary>no-intro header <c>name</c>, e.g. <c>Nintendo - Game Boy</c>.</summary>
    public string DatName { get; }

    /// <summary>no-intro header <c>version</c> (D25); metadat files have none.</summary>
    public string? Version { get; }

    public IReadOnlyList<DatRom> Roms { get; }

    /// <summary>Distinct base titles (§6.1) in DAT order.</summary>
    public IReadOnlyList<string> BaseTitles { get; }

    /// <summary>A titledb store's entry for an upper-case title id; null for a libretro store.</summary>
    public TitleDbRow? ByTitleId(string titleId) => _byTitleId.GetValueOrDefault(titleId);

    /// <summary>
    /// B4: the ROMs whose serial (any listed value) has <paramref name="code"/> as its second <c>-</c>-separated segment
    /// (case-insensitive; <c>RVL-SP3E-USA-B0</c> → <c>SP3E</c>), in DAT order, distinct by game name (first ROM wins).
    /// </summary>
    public IReadOnlyList<DatRom> BySerialCode(string code) =>
        _bySerialCode.TryGetValue(code, out var roms) ? roms : [];

    private void IndexSerials()
    {
        foreach (var rom in Roms)
        {
            if (rom.Serial is null)
                continue;
            foreach (var value in rom.Serial.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = value.Split('-');
                if (parts.Length < 2 || parts[1].Length == 0)
                    continue; // e.g. the Wii DAT's DS000101
                if (!_bySerialCode.TryGetValue(parts[1], out var list))
                    _bySerialCode[parts[1]] = list = [];
                if (!list.Exists(r => r.GameName == rom.GameName))
                    list.Add(rom);
            }
        }
    }

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
