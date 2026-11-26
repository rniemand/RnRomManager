using RnRomManager.Core.Dats;

namespace RnRomManager.Core.Tests.Dats;

public class DatStoreTests
{
    private static readonly NoIntroDat NoIntro = new("Nintendo - Game Boy", "v1",
    [
        new DatRom("Pokemon - Red Version (USA, Europe) (SGB Enhanced)", "Pokemon - Red Version (USA, Europe) (SGB Enhanced).gb", 1048576, "9f7fdd53", new string('a', 32), new string('1', 40)),
        new DatRom("Pokemon - Red Version (Hong Kong) (SGB Enhanced) (Pirate)", "x.gb", 1048576, "cee9c818", new string('b', 32), new string('2', 40)),
        new DatRom("Tetris (World) (Rev 1)", "Tetris (World) (Rev 1).gb", 32768, "46df91ad", new string('c', 32), new string('3', 40)),
    ]);

    private static MetadatFile Metadat(string field, params (string Crc, string Value)[] values) =>
        new("Nintendo - Game Boy", field, values.Select(v => new MetadatValue(v.Crc, v.Value)).ToList());

    [Test]
    public void BySha1_FindsTheRom()
    {
        var store = new DatStore(NoIntro, []);

        Assert.That(store.BySha1(new string('3', 40))?.GameName, Is.EqualTo("Tetris (World) (Rev 1)"));
        Assert.That(store.BySha1(new string('9', 40)), Is.Null);
        Assert.That((store.DatName, store.Version), Is.EqualTo(("Nintendo - Game Boy", "v1")));
    }

    private static readonly NoIntroDat Nes = new("Nintendo - Nintendo Entertainment System", "v1",
    [
        new DatRom("Pixel Pioneer (USA)", "Pixel Pioneer (USA).nes", 40976, "11111111", new string('a', 32), new string('4', 40)),
        new DatRom("Pixel Pioneer (USA)", "Pixel Pioneer (USA).unh", 40960, "22222222", new string('b', 32), new string('5', 40)),
        new DatRom("Headerless Only (Japan)", "Headerless Only (Japan).unh", 40960, "33333333", new string('c', 32), new string('6', 40)),
        new DatRom("Shouty (Europe)", "Shouty (Europe).NES", 40976, "44444444", new string('d', 32), new string('7', 40)),
    ]);

    [Test]
    public void RomExtensions_LeaveOtherExtensionsOutOfEveryIndex()
    {
        var store = new DatStore(Nes, [Metadat(DatKind.Genre, ("11111111", "Platform"), ("22222222", "Ignored"))], [".nes"]);

        Assert.That(store.BySha1(new string('4', 40))?.RomName, Is.EqualTo("Pixel Pioneer (USA).nes"));
        Assert.That(store.BySha1(new string('5', 40)), Is.Null);
        Assert.That(store.BySha1(new string('7', 40))?.RomName, Is.EqualTo("Shouty (Europe).NES"), "case-insensitive");
        Assert.That(store.Roms.Select(r => r.RomName), Is.EqualTo(new[] { "Pixel Pioneer (USA).nes", "Shouty (Europe).NES" }));
        Assert.That(store.BaseTitles, Is.EqualTo(new[] { "Pixel Pioneer", "Shouty" }));
        Assert.That(store.MetadataFor("11111111").Genres, Is.EqualTo(new[] { "Platform" }));
        Assert.That(store.MetadataFor("22222222"), Is.SameAs(DatMetadata.Empty));
    }

    [Test]
    public void NoRomExtensions_KeepEveryRom()
    {
        Assert.That(new DatStore(Nes, []).Roms, Has.Count.EqualTo(4));
    }

    [Test]
    public void BaseTitles_AreDistinctInDatOrder()
    {
        Assert.That(new DatStore(NoIntro, []).BaseTitles, Is.EqualTo(new[] { "Pokemon - Red Version", "Tetris" }));
    }

    [Test]
    public void SeveralMetadatBlocksPerCrc_AreMergedDistinctInFileOrder()
    {
        var store = new DatStore(NoIntro,
        [
            Metadat(DatKind.Developer, ("9f7fdd53", "Game Freak"), ("9f7fdd53", "Nintendo"), ("9f7fdd53", "Game Freak")),
            Metadat(DatKind.Publisher, ("9f7fdd53", "Nintendo")),
            Metadat(DatKind.Genre, ("9f7fdd53", "Role-Playing (RPG)"), ("9f7fdd53", "Music / Dancing")),
            Metadat(DatKind.ReleaseYear, ("9f7fdd53", "1998"), ("9f7fdd53", "1996")),
        ]);

        var meta = store.MetadataFor("9f7fdd53");
        Assert.That(meta.Developers, Is.EqualTo(new[] { "Game Freak", "Nintendo" }));
        Assert.That(meta.Publishers, Is.EqualTo(new[] { "Nintendo" }));
        Assert.That(meta.Genres, Is.EqualTo(new[] { "Role-Playing (RPG)", "Music / Dancing" }));
        Assert.That(meta.ReleaseYear, Is.EqualTo(1998), "first year wins");
    }

    [Test]
    public void UnparsableYear_IsSkipped_ForTheNextOne()
    {
        var store = new DatStore(NoIntro, [Metadat(DatKind.ReleaseYear, ("46df91ad", "19xx"), ("46df91ad", "1989"))]);

        Assert.That(store.MetadataFor("46df91ad").ReleaseYear, Is.EqualTo(1989));
    }

    [Test]
    public void MetadatCrcWithoutANoIntroRom_IsIgnored_UnknownCrcIsEmpty()
    {
        var store = new DatStore(NoIntro, [Metadat(DatKind.Developer, ("deadbeef", "Nobody"))]);

        Assert.That(store.MetadataFor("deadbeef"), Is.SameAs(DatMetadata.Empty));
        Assert.That(store.MetadataFor("46df91ad"), Is.SameAs(DatMetadata.Empty));
    }

    private static DatRom Wii(
        string game,
        string serial,
        char sha1,
        string ext = ".iso") =>
        new(game, game + ext, 1, "0000000" + sha1, new string(sha1, 32), new string(sha1, 40), serial);

    [Test]
    public void BySerialCode_MatchesTheSecondSegment_CaseInsensitive_DistinctByGame()
    {
        var store = new DatStore(new NoIntroDat("Nintendo - Wii", "v1",
        [
            Wii("Mario Kart Wii (Europe)", "RVL-RMCP-EUR", '1'),
            Wii("Mario Kart Wii (Europe)", "RVL-RMCP-EUR-B0", '1'), // repeated once per serial
            Wii("Sengoku (Japan)", "RVL-S6TJ-0-JPN-B0", '2'),
            Wii("Two Codes (USA)", " RVL-TWOE-USA , ,RVL-TWOE-USA-B0", '3'), // comma list, both values match
            Wii("Other (USA)", "RVL-RMCP-USA", '4'),
            Wii("Disc Channel", "DS000101", '5'),
            new DatRom("No Serial (USA)", "No Serial (USA).iso", 1, "00000006", new string('6', 32), new string('6', 40)),
            Wii("Filtered (USA)", "RVL-FILT-USA", '7', ".wbfs"),
        ]), [], [".iso"]);

        Assert.That(store.BySerialCode("rmcp").Select(r => r.GameName), Is.EqualTo(new[] { "Mario Kart Wii (Europe)", "Other (USA)" }));
        Assert.That(store.BySerialCode("S6TJ").Single().GameName, Is.EqualTo("Sengoku (Japan)"));
        Assert.That(store.BySerialCode("TWOE").Single().GameName, Is.EqualTo("Two Codes (USA)"));
        Assert.That(store.BySerialCode("DS000101"), Is.Empty, "a value with no '-' matches nothing");
        Assert.That(store.BySerialCode("FILT"), Is.Empty, "extension-filtered rom isn't indexed");
        Assert.That(store.BySerialCode("NONE"), Is.Empty);
    }

    private static readonly MameDat Mame = new(
    [
        new MameSet("sf2", "Street Fighter II - The World Warrior (World 910522)", "1991", "Capcom", null, null, false,
            [new MameRom("a", 100, "00000001", null, null, false), new MameRom("b", 28, "00000002", null, null, false)]),
        new MameSet("sf2ua", "Street Fighter II - The World Warrior (US 910206)", "19??", null, "sf2", "sf2", false, []),
        new MameSet("odd", "Odd Year", "198?", "", null, null, false, []),
    ]);

    [Test]
    public void Mame_OneRomPerSet_NamedBySetWithEmptyHashes()
    {
        var store = new DatStore(Mame, DatStore.MameName, "2026-01-01T00:00:00Z", ".mamez");

        Assert.That((store.DatName, store.Version), Is.EqualTo(("MAME", "2026-01-01T00:00:00Z")));
        Assert.That(store.Roms[0], Is.EqualTo(new DatRom("Street Fighter II - The World Warrior (World 910522)", "sf2.mamez", 128, "", "", "")));
        Assert.That(store.Roms.Select(r => r.RomName), Is.EqualTo(new[] { "sf2.mamez", "sf2ua.mamez", "odd.mamez" }));
        Assert.That(store.BaseTitles, Is.EqualTo(new[] { "Street Fighter II - The World Warrior", "Odd Year" }));
        Assert.That(store.BySha1(new string('0', 40)), Is.Null);
    }

    // FBNeo D5: the DatName and ROM extension come from the system.
    [Test]
    public void Romset_UsesTheGivenDatNameAndRomExtension()
    {
        var store = new DatStore(Mame, "FBNeo - Arcade Games", "v", ".fbneoz");

        Assert.That(store.DatName, Is.EqualTo("FBNeo - Arcade Games"));
        Assert.That(store.Roms.Select(r => r.RomName), Is.EqualTo(new[] { "sf2.fbneoz", "sf2ua.fbneoz", "odd.fbneoz" }));
        Assert.That(store.BySetName("sf2")?.Name, Is.EqualTo("sf2"));
    }

    [Test]
    public void Mame_BySetName_IgnoresCase()
    {
        var store = new DatStore(Mame, DatStore.MameName, "v", ".mamez");

        Assert.That(store.BySetName("SF2UA")?.Name, Is.EqualTo("sf2ua"));
        Assert.That(store.BySetName("nope"), Is.Null);
        Assert.That(new DatStore(NoIntro, []).BySetName("sf2"), Is.Null);
    }

    [Test]
    public void Mame_MetadataForSet_YearAndManufacturer()
    {
        var store = new DatStore(Mame, DatStore.MameName, "v", ".mamez");

        var sf2 = store.MetadataForSet("sf2");
        Assert.That((sf2.ReleaseYear, sf2.Developers, sf2.Publishers, sf2.Genres), Is.EqualTo((1991, new[] { "Capcom" }, new[] { "Capcom" }, Array.Empty<string>())));
        var clone = store.MetadataForSet("sf2ua");
        Assert.That((clone.ReleaseYear, clone.Developers.Count), Is.EqualTo(((int?)null, 0)));
        Assert.That(store.MetadataForSet("odd").ReleaseYear, Is.Null);
        Assert.That(store.MetadataForSet("unknown"), Is.SameAs(DatMetadata.Empty));
    }
}
