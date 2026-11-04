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
