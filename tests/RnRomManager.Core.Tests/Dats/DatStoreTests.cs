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
}
