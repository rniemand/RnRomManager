using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Sega.Tests;

/// <summary>Master System spec M1, M2 and U2.</summary>
public class MasterSystemHandlerTests
{
    private static readonly ISystemHandler Handler = new MasterSystemHandler();

    [Test]
    public void BrowserCore_IsSmsplus_WithEmbeddedRoms()
    {
        Assert.That(Handler.BrowserCore, Is.EqualTo("smsplus"));
        Assert.That(Handler.EmbedRomsInZip, Is.True);
    }

    private static byte[] Rom(int size, int offset, byte regionSize = 0x4C)
    {
        var rom = new byte[size];
        Encoding.ASCII.GetBytes("TMR SEGA").CopyTo(rom, offset);
        rom[offset + 15] = regionSize;
        return rom;
    }

    [Test]
    public void MasterSystem_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("sms", "Master System", "SMS", "mastersystem", "Sega - Master System - Mark III")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".sms" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((2048L, 4194304L)));
    }

    [Test]
    public void ReadHeader_SmsExport_IsClassifiedSms()
    {
        var header = Handler.ReadHeader(Rom(32768, 0x7FF0))!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("sms", (string?)null, true)));
        Assert.That((header.Fields["offset"], header.Fields["region"], header.Fields["sizeCode"]), Is.EqualTo(("7FF0", "SMS Export", 12)));
    }

    [Test]
    public void ReadHeader_GgRegion_IsStillSms()
    {
        var header = Handler.ReadHeader(Rom(32768, 0x7FF0, 0x6C))!;
        Assert.That((header.ClassifiedSystemId, header.Fields["region"]), Is.EqualTo(("sms", "GG Export")));
    }

    [Test]
    public void ReadHeader_At3FF0() =>
        Assert.That(Handler.ReadHeader(Rom(16384, 0x3FF0))!.Fields["offset"], Is.EqualTo("3FF0"));

    [Test]
    public void ReadHeader_NoSignatureOrEmpty_IsNull()
    {
        Assert.That(Handler.ReadHeader(new byte[32768]), Is.Null);
        Assert.That(Handler.ReadHeader(ReadOnlySpan<byte>.Empty), Is.Null);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var rom = new byte[8192];
        new Random(7).NextBytes(rom);
        Assert.That(Handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheGameGearOne()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new GameGearHandler().DefaultBoxArtPng.ToArray()));
    }
}
