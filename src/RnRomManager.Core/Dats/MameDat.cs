using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace RnRomManager.Core.Dats;

/// <summary>Arcade D2: one ROM of a MAME set. <see cref="Crc32"/> is lower-case hex; null only for a nodump ROM.</summary>
public sealed record MameRom(string Name, long Size, string? Crc32, string? Merge, string? Bios, bool NoDump);

/// <summary>Arcade D2: one <c>&lt;game&gt;</c> (or <c>&lt;machine&gt;</c>) of a MAME <c>-listxml</c> DAT.</summary>
public sealed record MameSet(
    string Name, string Description, string? Year, string? Manufacturer, string? CloneOf, string? RomOf, bool IsBios, IReadOnlyList<MameRom> Roms);

/// <summary>Arcade D2: a MAME <c>-listxml</c> DAT, sets in file order.</summary>
public sealed record MameDat(IReadOnlyList<MameSet> Sets)
{
    /// <exception cref="DatParseException">A set without a name or description, a crc-less ROM that isn't nodump, a duplicate set name, or no sets.</exception>
    public static MameDat Parse(Stream stream)
    {
        // The file starts with an inline DTD: ignored, never processed, and no resolver, so no entity expansion or fetch.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
        var sets = new List<MameSet>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            reader.MoveToContent();
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name is "game" or "machine")
                {
                    var set = ReadSet((XElement)XNode.ReadFrom(reader));
                    if (!names.Add(set.Name))
                        throw new DatParseException($"Duplicate game '{set.Name}'.");
                    sets.Add(set);
                }
                else if (reader.NodeType == XmlNodeType.Element)
                    reader.Skip();
                else
                    reader.Read();
            }
        }
        catch (XmlException e)
        {
            throw new DatParseException($"Invalid XML: {e.Message}");
        }
        if (sets.Count == 0)
            throw new DatParseException("No <game> elements found.");
        return new MameDat(sets);
    }

    private static MameSet ReadSet(XElement game)
    {
        var name = (string?)game.Attribute("name");
        if (string.IsNullOrEmpty(name))
            throw new DatParseException("A game has no name.");
        var description = (string?)game.Element("description");
        if (string.IsNullOrEmpty(description))
            throw new DatParseException($"Game '{name}' has no description.");
        var roms = game.Elements("rom").Select(r => ReadRom(name, r)).ToList();
        return new MameSet(
            name, description, (string?)game.Element("year"), (string?)game.Element("manufacturer"),
            (string?)game.Attribute("cloneof"), (string?)game.Attribute("romof"),
            (string?)game.Attribute("runnable") == "no" || (string?)game.Attribute("isbios") == "yes",
            roms);
    }

    private static MameRom ReadRom(string game, XElement rom)
    {
        var name = (string?)rom.Attribute("name") ?? "";
        var noDump = (string?)rom.Attribute("status") == "nodump";
        var crc = ((string?)rom.Attribute("crc"))?.ToLowerInvariant();
        if (crc is null && !noDump)
            throw new DatParseException($"Game '{game}': ROM '{name}' has no crc.");
        var sizeText = (string?)rom.Attribute("size");
        long size = 0;
        if (sizeText is not null && !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out size))
            throw new DatParseException($"Game '{game}': ROM '{name}' has an invalid size '{sizeText}'.");
        return new MameRom(name, size, crc, (string?)rom.Attribute("merge"), (string?)rom.Attribute("bios"), noDump);
    }
}
