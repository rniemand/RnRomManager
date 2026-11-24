using System.Text;
using RnRomManager.Core.Dats;

namespace RnRomManager.Core.Tests.Dats;

public class MameDatTests
{
    private const string Dtd = """
        <?xml version="1.0"?>
        <!DOCTYPE mame [
        <!ELEMENT mame (game+)>
        <!ATTLIST game name CDATA #REQUIRED>
        ]>
        """;

    private static MameDat Parse(string xml) => MameDat.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    private static string Wrap(string games) => Dtd + "\n<mame build=\"0.78\">\n" + games + "\n</mame>";

    [Test]
    public void Parse_ReadsParentsClonesBiosAndRomAttributes()
    {
        var dat = Parse(Wrap("""
            <game name="neogeo" runnable="no">
              <description>Neo-Geo</description>
              <year>1990</year>
              <manufacturer>SNK</manufacturer>
              <rom name="asia-s3.rom" size="131072" crc="91B64BE3" bios="asia"/>
              <rom name="sm1.sm1" size="131072" crc="97cf998b"/>
            </game>
            <game name="mslug" romof="neogeo">
              <description>Metal Slug - Super Vehicle-001</description>
              <year>1996</year>
              <manufacturer>Nazca</manufacturer>
              <rom name="201-p1.bin" size="2097152" crc="08d8daa5"/>
              <rom name="sm1.sm1" merge="sm1.sm1" size="131072" crc="97cf998b"/>
              <rom name="bad.bin" size="1024" status="nodump"/>
              <sample name="ignored"/>
              <driver status="good"/>
            </game>
            <game name="sf2ua" cloneof="sf2" romof="sf2">
              <description>Street Fighter II - The World Warrior (US 910206)</description>
              <rom name="sf2u.30a" size="131072" crc="08beb861"/>
            </game>
            <game name="bios2" isbios="yes"><description>Some BIOS</description></game>
            """));

        Assert.That(dat.Sets.Select(s => s.Name), Is.EqualTo(new[] { "neogeo", "mslug", "sf2ua", "bios2" }));
        var neogeo = dat.Sets[0];
        Assert.That((neogeo.IsBios, neogeo.Year, neogeo.Manufacturer, neogeo.CloneOf, neogeo.RomOf), Is.EqualTo((true, "1990", "SNK", (string?)null, (string?)null)));
        Assert.That(neogeo.Roms[0], Is.EqualTo(new MameRom("asia-s3.rom", 131072, "91b64be3", null, "asia", false)));
        var mslug = dat.Sets[1];
        Assert.That((mslug.IsBios, mslug.RomOf, mslug.Description), Is.EqualTo((false, "neogeo", "Metal Slug - Super Vehicle-001")));
        Assert.That(mslug.Roms, Is.EqualTo(new[]
        {
            new MameRom("201-p1.bin", 2097152, "08d8daa5", null, null, false),
            new MameRom("sm1.sm1", 131072, "97cf998b", "sm1.sm1", null, false),
            new MameRom("bad.bin", 1024, null, null, null, true),
        }));
        Assert.That((dat.Sets[2].CloneOf, dat.Sets[2].RomOf, dat.Sets[2].Year), Is.EqualTo(("sf2", "sf2", (string?)null)));
        Assert.That((dat.Sets[3].IsBios, dat.Sets[3].Roms.Count), Is.EqualTo((true, 0)));
    }

    [Test]
    public void Parse_AcceptsMachineElements()
    {
        var dat = Parse("<mame><machine name=\"pacman\"><description>Pac-Man</description><rom name=\"a\" size=\"1\" crc=\"0000000a\"/></machine></mame>");

        Assert.That(dat.Sets.Single().Name, Is.EqualTo("pacman"));
    }

    [TestCase("<game><description>X</description></game>", "A game has no name.")]
    [TestCase("<game name=\"x\"></game>", "Game 'x' has no description.")]
    [TestCase("<game name=\"x\"><description>X</description><rom name=\"r\" size=\"1\"/></game>", "Game 'x': ROM 'r' has no crc.")]
    [TestCase("<game name=\"x\"><description>X</description></game><game name=\"X\"><description>Y</description></game>", "Duplicate game 'X'.")]
    [TestCase("", "No <game> elements found.")]
    public void Parse_RefusesBadFiles(string games, string message)
    {
        var e = Assert.Throws<DatParseException>(() => Parse(Wrap(games)));
        Assert.That(e!.Message, Is.EqualTo(message));
    }

    [Test]
    public void Parse_DoesNotResolveExternalEntities()
    {
        var secret = Path.GetTempFileName();
        try
        {
            File.WriteAllText(secret, "LEAKED");
            var xml = $"""
                <?xml version="1.0"?>
                <!DOCTYPE mame [ <!ENTITY ext SYSTEM "{new Uri(secret).AbsoluteUri}"> ]>
                <mame><game name="x"><description>&ext;</description></game></mame>
                """;

            var dat = TryParse(xml);

            Assert.That(dat?.Sets.Single().Description ?? "", Does.Not.Contain("LEAKED"));
        }
        finally
        {
            File.Delete(secret);
        }
    }

    // An ignored DTD leaves &ext; undeclared: either an XML error (DatParseException) or no expansion is safe.
    private static MameDat? TryParse(string xml)
    {
        try
        {
            return Parse(xml);
        }
        catch (DatParseException)
        {
            return null;
        }
    }
}
