using System.Text;
using RnRomManager.TestData;

namespace RnRomManager.Systems.NintendoDisc.Tests;

/// <summary>Disc systems B6: the GameCube/Wii disc header.</summary>
public class NintendoDiscHeaderTests
{
    private static byte[] Disc(string gameId = "GALE01", byte disc = 0, byte version = 2, string title = "Super Smash Bros. Melee",
        bool gameCube = false, bool wii = false, int length = 0x440)
    {
        var rom = new byte[length];
        Encoding.ASCII.GetBytes(gameId).CopyTo(rom, 0);
        rom[6] = disc;
        rom[7] = version;
        if (wii)
            new byte[] { 0x5D, 0x1C, 0x9E, 0xA3 }.CopyTo(rom, 0x18);
        if (gameCube)
            new byte[] { 0xC2, 0x33, 0x9F, 0x3D }.CopyTo(rom, 0x1C);
        Encoding.Latin1.GetBytes(title).CopyTo(rom, 0x20);
        return rom;
    }

    [Test]
    public void GameCubeMagic_IsAPlausibleGc_WithEveryField()
    {
        var header = NintendoDiscHeader.Read(Disc(disc: 1, gameCube: true), "wii");

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("gc", "Super Smash Bros. Melee", true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["gameId"] = "GALE01", ["discNumber"] = (byte)1, ["version"] = (byte)2, ["title"] = "Super Smash Bros. Melee",
            ["container"] = "iso",
        }));
    }

    [Test]
    public void WiiMagic_IsAPlausibleWii_EvenFromTheGameCubeFolder()
    {
        var header = NintendoDiscHeader.Read(Disc("RSBE01", title: "  Brawl  ", wii: true), "gc");

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("wii", "Brawl", true)));
    }

    [Test]
    public void NoMagic_IsNotPlausible_AndClassifiedAsTheCaller() =>
        Assert.That(NintendoDiscHeader.Read(Disc(), "gc") is { ClassifiedSystemId: "gc", IsPlausible: false }, Is.True);

    [TestCase(0)]
    [TestCase(5)]
    [TestCase(7)]
    [TestCase(0x1F)]
    [TestCase(0x30)]
    public void TruncatedSpan_GivesNullForTheFieldsItCuts_AndNeverThrows(int length)
    {
        var rom = Disc(title: new string('T', 40), gameCube: true)[..length];

        var header = NintendoDiscHeader.Read(rom, "gc");

        Assert.That(header.IsPlausible, Is.EqualTo(length >= 0x20), "the magic at 0x1C needs 0x20 bytes");
        Assert.That(header.Fields["gameId"], length >= 6 ? Is.EqualTo("GALE01") : Is.Null);
        Assert.That(header.Fields["discNumber"], length >= 7 ? Is.Not.Null : Is.Null);
        Assert.That(header.Fields["version"], length >= 8 ? Is.Not.Null : Is.Null);
        Assert.That(header.Title, Is.Null, "the span ends inside the title");
    }

    [Test]
    public void NonPrintableGameId_IsNull() =>
        Assert.That(NintendoDiscHeader.Read(Disc("GA\u0001E01", gameCube: true), "gc").Fields["gameId"], Is.Null);

    [Test]
    public void Title_IsCutAtTheNul_AndDecodedAsLatin1()
    {
        var rom = Disc(title: "Pokémon\0Colosseum", gameCube: true);

        Assert.That(NintendoDiscHeader.Read(rom, "gc").Title, Is.EqualTo("Pokémon"));
    }

    [Test]
    public void Title_WithoutANul_StopsAt0x3E0Bytes()
    {
        var rom = Disc(title: new string('A', 0x400), gameCube: true, length: 0x500);

        Assert.That(NintendoDiscHeader.Read(rom, "gc").Title, Is.EqualTo(new string('A', 0x3E0)));
    }

    private static SyntheticDiscBuilder WiiDisc(string title = "WAVE BOWLING") =>
        new() { Seed = 7, Magic = NintendoDiscMagic.Wii, GameId = "RWBE01", Title = title, DiscNumber = 1, Version = 2 };

    [TestCase(9)]
    [TestCase(12)]
    public void Wbfs_IsReadFromTheDiscHeaderCopy(int shift)
    {
        var header = NintendoDiscHeader.Read(WiiDisc().Wbfs(shift), "gc");

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("wii", "WAVE BOWLING", true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["gameId"] = "RWBE01", ["discNumber"] = (byte)1, ["version"] = (byte)2, ["title"] = "WAVE BOWLING", ["container"] = "wbfs",
        }));
    }

    [TestCase(8)]
    [TestCase(13)]
    [TestCase(0xFF)]
    public void Wbfs_WithABadSectorShift_IsNotPlausible(int shift)
    {
        var wbfs = WiiDisc().Wbfs();
        wbfs[8] = (byte)shift;

        AssertUnreadableWbfs(NintendoDiscHeader.Read(wbfs, "wii"));
    }

    [TestCase(4)]
    [TestCase(9)]
    [TestCase(0x200 + 0xFF)]
    public void Wbfs_WithATruncatedCopy_IsNotPlausible(int length) =>
        AssertUnreadableWbfs(NintendoDiscHeader.Read(WiiDisc().Wbfs()[..length], "wii"));

    [Test]
    public void Wbfs_TitleFillingTheCopyWithoutANul_IsKeptWhole() =>
        Assert.That(NintendoDiscHeader.Read(WiiDisc(new string('A', 0x100)).Wbfs(), "wii").Title, Is.EqualTo(new string('A', 0xE0)),
            "the title never runs into the block table after the copy");

    private static void AssertUnreadableWbfs(RnRomManager.Core.Systems.RomHeader header)
    {
        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("wii", (string?)null, false)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["gameId"] = null, ["discNumber"] = null, ["version"] = null, ["title"] = null, ["container"] = "wbfs",
        }));
    }
}
