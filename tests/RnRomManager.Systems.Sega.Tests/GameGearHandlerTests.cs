using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Systems;
using RnRomManager.Systems.Coleco;

namespace RnRomManager.Systems.Sega.Tests;

/// <summary>Game Gear spec G2, G3 and G6.</summary>
public class GameGearHandlerTests
{
    private static readonly ISystemHandler Handler = new GameGearHandler();

    [Test]
    public void BrowserCore_IsSmsplus_WithEmbeddedRoms()
    {
        Assert.That(Handler.BrowserCore, Is.EqualTo("smsplus"));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
    }

    private static byte[] Rom(int size, int offset, byte regionSize = 0x6C)
    {
        var rom = new byte[size];
        Encoding.ASCII.GetBytes("TMR SEGA").CopyTo(rom, offset);
        rom[offset + 10] = 0x34;
        rom[offset + 11] = 0x12;
        rom[offset + 12] = 0x25;
        rom[offset + 13] = 0x00;
        rom[offset + 14] = 0x01;
        rom[offset + 15] = regionSize;
        return rom;
    }

    [Test]
    public void GameGear_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("gg", "Game Gear", "GG", "gamegear", "Sega - Game Gear")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".gg", ".sms" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((8192L, 1048576L)));
    }

    [Test]
    public void ReadHeader_At7FF0_ReadsEveryField()
    {
        var header = Handler.ReadHeader(Rom(32768, 0x7FF0))!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("gg", (string?)null, true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["offset"] = "7FF0", ["checksum"] = "1234", ["productCode"] = "25", ["version"] = 1, ["region"] = "GG Export", ["sizeCode"] = 12,
        }));
    }

    [TestCase(16384, 0x3FF0, "3FF0")]
    [TestCase(8192, 0x1FF0, "1FF0")]
    public void ReadHeader_FindsTheLowerOffsets(int size, int offset, string expected) =>
        Assert.That(Handler.ReadHeader(Rom(size, offset))!.Fields["offset"], Is.EqualTo(expected));

    [Test]
    public void ReadHeader_PrefersTheHighestOffset()
    {
        var rom = Rom(32768, 0x7FF0);
        Encoding.ASCII.GetBytes("TMR SEGA").CopyTo(rom, 0x3FF0);
        Assert.That(Handler.ReadHeader(rom)!.Fields["offset"], Is.EqualTo("7FF0"));
    }

    [Test]
    public void ReadHeader_NoSignatureOrEmpty_IsNull()
    {
        Assert.That(Handler.ReadHeader(new byte[32768]), Is.Null);
        Assert.That(Handler.ReadHeader(ReadOnlySpan<byte>.Empty), Is.Null);
        Assert.That(Handler.ReadHeader(Rom(32768, 0x7FF0).AsSpan(0, 0x7FF0 + 15)), Is.Null, "too short for the header");
    }

    [Test]
    public void ReadHeader_UnknownRegion_IsTheNibble() =>
        Assert.That(Handler.ReadHeader(Rom(32768, 0x7FF0, 0x9C))!.Fields["region"], Is.EqualTo("9"));

    [Test]
    public void ReadHeader_AllZeroProductCode_IsZero()
    {
        var rom = Rom(32768, 0x7FF0);
        rom[0x7FF0 + 12] = rom[0x7FF0 + 14] = 0;
        Assert.That(Handler.ReadHeader(rom)!.Fields["productCode"], Is.EqualTo("0"));
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var rom = new byte[8192];
        new Random(7).NextBytes(rom);
        Assert.That(Handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheColecoOne()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new ColecoVisionHandler().DefaultBoxArtPng.ToArray()));
    }
}
