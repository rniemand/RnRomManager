using RnRomManager.Core.Dats;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Tests.Dats;

/// <summary>Spec §15.1 "DAT parser" row (the cache parts are in <see cref="DatCacheTests"/>).</summary>
public class DatParserTests
{
    // Same shape as the libretro files: tabs, upper-case hashes, optional region/serial/release fields.
    private const string NoIntro = """
        clrmamepro (
        	name "Nintendo - Game Boy"
        	description "Nintendo - Game Boy"
        	version "2026.10.07"
        	extensions "gb"
        	homepage "http://github.com/robloach/libretro-dats"
        )

        game (
        	name "Tetris (World) (Rev 1)"
        	rom ( name "Tetris (World) (Rev 1).gb" size 32768 crc 46DF91AD md5 982ED5D2B12A0377EB14BCDC4123744E sha1 74591CC9501AF93873F9A5D3EB12DA12C0723BBC )
        )
        game (
        	name "Tetris 2 (USA)"
        	region "USA"
        	serial "DMG-TP-USA"
        	releaseyear "1993"
        	rom ( name "Tetris 2 (USA).gb" size 131072 crc AA4A1EDB md5 9DDC6F4F0DC369008CBD2AC4EC561E75 sha1 EC7B3419C08F1A5713AFFF2B26C1534D91602338 )
        )
        game (
        	name "Yelu Wangzi (Prince Yeh Rude) (Taiwan) (Unl)"
        	rom ( name "Yelu Wangzi (Prince Yeh Rude) (Taiwan) (Unl).gb" size 65536 crc 00000001 md5 00000000000000000000000000000001 sha1 0000000000000000000000000000000000000001 )
        )
        """;

    [Test]
    public void Header_GivesNameAndVersion()
    {
        var dat = DatFiles.ParseNoIntro(NoIntro);

        Assert.That((dat.DatName, dat.Version), Is.EqualTo(("Nintendo - Game Boy", "2026.10.07")));
    }

    [Test]
    public void Games_WithAndWithoutRegion_AndUnknownFieldsIgnored()
    {
        var roms = DatFiles.ParseNoIntro(NoIntro).Roms;

        Assert.That(roms.Select(r => r.GameName), Is.EqualTo(new[] { "Tetris (World) (Rev 1)", "Tetris 2 (USA)", "Yelu Wangzi (Prince Yeh Rude) (Taiwan) (Unl)" }));
        Assert.That(roms[1], Is.EqualTo(new DatRom("Tetris 2 (USA)", "Tetris 2 (USA).gb", 131072, "aa4a1edb",
            "9ddc6f4f0dc369008cbd2ac4ec561e75", "ec7b3419c08f1a5713afff2b26c1534d91602338", "DMG-TP-USA")));
        Assert.That(roms[0].Serial, Is.Null, "no serial");
    }

    [Test]
    public void Serial_FromTheRomBlock_ElseTheGameBlock()
    {
        const string text = """
            clrmamepro ( name "Nintendo - Wii" )
            game (
                name "A (USA)"
                serial "RVL-GAME-USA"
                rom ( name "A (USA).iso" size 1 crc 00000001 md5 00000000000000000000000000000001 sha1 0000000000000000000000000000000000000001 serial "RVL-ROMS-USA" )
            )
            game (
                name "B (USA)"
                serial "RVL-BBBE-USA, RVL-BBBP-EUR"
                rom ( name "B (USA).iso" size 1 crc 00000002 md5 00000000000000000000000000000002 sha1 0000000000000000000000000000000000000002 )
            )
            """;

        var roms = DatFiles.ParseNoIntro(text).Roms;

        Assert.That(roms.Select(r => r.Serial), Is.EqualTo(new[] { "RVL-ROMS-USA", "RVL-BBBE-USA, RVL-BBBP-EUR" }));
    }

    [Test]
    public void QuotedNamesContainingParentheses_StayWhole()
    {
        Assert.That(DatFiles.ParseNoIntro(NoIntro).Roms[2].RomName, Is.EqualTo("Yelu Wangzi (Prince Yeh Rude) (Taiwan) (Unl).gb"));
    }

    [Test]
    public void Metadat_ValuesAreNeverSplit_AndUnknownFieldsIgnored()
    {
        const string text = """
            clrmamepro (
            	name "Nintendo - Game Boy"
            	description "Nintendo - Game Boy"
            )
            game (
            	comment "Some Game (USA)"
            	franchise "Something"
            	genre "Music / Dancing"
            	rom ( crc ABCDEF01 )
            )
            game (
            	comment "No CRC here"
            	genre "Lost"
            )
            """;

        var file = DatFiles.ParseMetadat(text, "genre");

        Assert.That(file.Values, Is.EqualTo(new[] { new MetadatValue("abcdef01", "Music / Dancing") }));
        Assert.That(file.DatName, Is.EqualTo("Nintendo - Game Boy"));
    }

    [TestCase("clrmamepro ( name \"x\" ", "Missing ')'")]
    [TestCase("clrmamepro ( name \"x ) ", "Unterminated string")]
    [TestCase("clrmamepro ( name \"x\" ) )", "Unexpected ')'")]
    [TestCase("clrmamepro ( name ) ", "has no value")]
    [TestCase("( name \"x\" )", "Expected a key")]
    public void MalformedInput_Throws(string text, string message)
    {
        var ex = Assert.Throws<DatParseException>(() => ClrMameProParser.Parse(text));

        Assert.That(ex!.Message, Does.Contain(message));
    }

    [TestCase("")]
    [TestCase("game ( name \"x\" )")]
    public void MissingHeader_Throws(string text) =>
        Assert.Throws<DatParseException>(() => DatFiles.ParseNoIntro(text));

    [TestCase("sha1 XYZ")]
    [TestCase("sha1 74591CC9501AF93873F9A5D3EB12DA12C0723BB")]
    [TestCase("")]
    public void BadOrMissingRomField_Throws(string sha1)
    {
        var text = $"clrmamepro ( name \"n\" ) game ( name \"g\" rom ( name \"g.gb\" size 1 crc 00000000 md5 00000000000000000000000000000000 {sha1} ) )";

        Assert.Throws<DatParseException>(() => DatFiles.ParseNoIntro(text));
    }

    [Test]
    public void ErrorMessages_NameTheLine()
    {
        var ex = Assert.Throws<DatParseException>(() => ClrMameProParser.Parse("clrmamepro (\n\tname \"x\"\n)\n)"));

        Assert.That(ex!.Message, Does.Contain("line 4"));
    }

    [TestCase("", "00000000")]
    [TestCase("123456789", "cbf43926")] // the standard CRC-32 check value
    [TestCase("The quick brown fox jumps over the lazy dog", "414fa339")]
    public void Crc32_MatchesKnownVectors(string input, string expected) =>
        Assert.That(Crc32.ToHex(System.Text.Encoding.ASCII.GetBytes(input)), Is.EqualTo(expected));
}
