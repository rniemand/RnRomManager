using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Nes.Tests;

/// <summary>Famicom Disk System: the handler contract, the fwNES header strip and the disk info block.</summary>
public class FdsHandlerTests
{
    private static readonly ISystemHandler Handler = new FdsHandler();

    /// <summary>A headerless two-sided image whose side 1 starts with a disk info block for game code <c>ABC</c>.</summary>
    internal static byte[] Disk()
    {
        var disk = new byte[2 * FdsHandler.SideBytes];
        new Random(5).NextBytes(disk);
        disk[0] = 0x01;
        "*NINTENDO-HVC*"u8.CopyTo(disk.AsSpan(1));
        disk[0x0F] = 0xA4;
        "ABC"u8.CopyTo(disk.AsSpan(0x10));
        disk[0x14] = 1;
        return disk;
    }

    [Test]
    public void Fds_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("fds", "Famicom Disk System", "FDS", "fds", "Nintendo - Family Computer Disk System")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".fds" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((65_500L, 16L + 8 * 65_500)));
        Assert.That((Handler.EmbedRomsInZip, Handler.DatSource, Handler.DatFolder), Is.EqualTo((true, DatSource.Libretro, DatKind.NoIntro)));
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
        Assert.That(Handler.BrowserCore, Is.EqualTo("fceumm"));
    }

    [Test]
    public void NormalizeForHashing_StripsTheFwNesHeader_AndKeepsAHeaderlessImage()
    {
        var disk = Disk();
        byte[] headered = [(byte)'F', (byte)'D', (byte)'S', 0x1A, 2, .. new byte[11], .. disk];

        Assert.That(Handler.NormalizeForHashing(headered).ToArray(), Is.EqualTo(disk));
        HandlerAsserts.AssertNormalizeIsIdentity(Handler, disk);
    }

    [Test]
    public void ReadHeader_ReadsTheDiskInfoBlock()
    {
        var header = Handler.ReadHeader(Disk())!;

        Assert.That((header.ClassifiedSystemId, header.IsPlausible), Is.EqualTo(("fds", true)));
        Assert.That((header.Fields["gameCode"], header.Fields["manufacturerCode"], header.Fields["revision"], header.Fields["sides"]),
            Is.EqualTo(("ABC", (byte)0xA4, (byte)1, 2)));
    }

    [Test]
    public void ReadHeader_WithoutADiskInfoBlock_IsImplausible()
    {
        var junk = new byte[FdsHandler.SideBytes];

        Assert.That(Handler.ReadHeader(junk)!.IsPlausible, Is.False);
        HandlerAsserts.AssertNoHeader(Handler, new byte[8]);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        HandlerAsserts.AssertBoxArtPng(Handler, 512);
    }
}
