using System.Globalization;

namespace RnRomManager.Core.Dats;

/// <summary>Turns parsed clrmamepro text into the no-intro and metadat models (spec §10.1). Unknown fields are ignored.</summary>
public static class DatFiles
{
    /// <exception cref="DatParseException">Bad syntax, no header name, or a ROM missing name/size/crc/md5/sha1.</exception>
    public static NoIntroDat ParseNoIntro(string text)
    {
        var root = ClrMameProParser.Parse(text);
        var header = Header(root);
        var roms = new List<DatRom>();
        foreach (var game in root.Blocks("game"))
        {
            var gameName = game.Value("name") ?? throw new DatParseException("A game has no name.");
            foreach (var rom in game.Blocks("rom"))
            {
                roms.Add(new DatRom(
                    gameName,
                    Required(rom, "name", gameName),
                    long.TryParse(Required(rom, "size", gameName), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                        ? size
                        : throw new DatParseException($"ROM of '{gameName}' has an invalid size."),
                    Hex(rom, "crc", 8, gameName),
                    Hex(rom, "md5", 32, gameName),
                    Hex(rom, "sha1", 40, gameName)));
            }
        }
        return new NoIntroDat(header.Value("name")!, header.Value("version"), roms);
    }

    /// <param name="field"><c>releaseyear</c>, <c>developer</c>, <c>publisher</c> or <c>genre</c>.</param>
    public static MetadatFile ParseMetadat(string text, string field)
    {
        var root = ClrMameProParser.Parse(text);
        var header = Header(root);
        var values = new List<MetadatValue>();
        foreach (var game in root.Blocks("game"))
        {
            var crc = game.Blocks("rom").Select(r => r.Value("crc")).FirstOrDefault(c => c is not null);
            // Every value of the field is kept whole ("Music / Dancing", "Hiro Co., Ltd.").
            foreach (var value in game.Items.Where(i => i.Key == field).Select(i => i.Value).OfType<string>())
            {
                if (crc is not null && value.Length > 0)
                    values.Add(new MetadatValue(crc.ToLowerInvariant(), value));
            }
        }
        return new MetadatFile(header.Value("name")!, field, values);
    }

    private static DatBlock Header(DatBlock root) =>
        root.Blocks("clrmamepro").FirstOrDefault() is { } header && header.Value("name") is not null
            ? header
            : throw new DatParseException("Missing clrmamepro header with a name.");

    private static string Required(DatBlock rom, string key, string gameName) =>
        rom.Value(key) ?? throw new DatParseException($"ROM of '{gameName}' has no {key}.");

    private static string Hex(DatBlock rom, string key, int length, string gameName)
    {
        var value = Required(rom, key, gameName);
        return value.Length == length && value.All(char.IsAsciiHexDigit)
            ? value.ToLowerInvariant()
            : throw new DatParseException($"ROM of '{gameName}' has an invalid {key}.");
    }
}
