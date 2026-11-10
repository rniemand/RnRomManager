using System.Buffers.Binary;
using RnRomManager.Core.Systems;
using RnRomManager.Systems.Atari;

namespace RnRomManager.Systems.Nec.Tests;

/// <summary>TurboGrafx-16 spec T1, T2 and T5.</summary>
public class TurboGrafx16HandlerTests
{
    private static readonly ISystemHandler Handler = new TurboGrafx16Handler();

    [Test]
    public void TurboGrafx16_Contract()
    {
        Assert.That(Handler.ShortName, Is.EqualTo("TG16"));
        Assert.That((Handler.Id, Handler.DisplayName, Handler.Family, Handler.DatName),
            Is.EqualTo(("tg16", "TurboGrafx-16", "tg16", "NEC - PC Engine - TurboGrafx 16")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".pce" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((73728L, 2621952L)));
    }

    [TestCase(0)]
    [TestCase(73728)]
    [TestCase(262144)]
    [TestCase(262656)]
    public void ReadHeader_IsAlwaysNull(int size)
    {
        var rom = new byte[size];
        new Random(size).NextBytes(rom);
        Assert.That(Handler.ReadHeader(rom), Is.Null);
    }

    [Test]
    public void NormalizeForHashing_StripsA512ByteCopierHeader()
    {
        var rom = new byte[262144 + 512];
        new Random(7).NextBytes(rom);
        Assert.That(Handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom[512..]));
    }

    [Test]
    public void NormalizeForHashing_LeavesAnUnheaderedDumpUnchanged()
    {
        var rom = new byte[262144];
        new Random(7).NextBytes(rom);
        Assert.That(Handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom));
    }

    [Test]
    public void NormalizeForHashing_StripsByLengthAlone_EvenAnUnheaderedProtoOfThatSize()
    {
        // Gnop / Masteroids are 115,200 bytes unheadered (115,200 % 8192 == 512): the accepted cost of spec T2.
        var rom = new byte[115200];
        new Random(7).NextBytes(rom);
        Assert.That(Handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom[512..]));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromTheAtariOne()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new Atari2600Handler().DefaultBoxArtPng.ToArray()));
    }
}
