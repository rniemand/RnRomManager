using System.Buffers.Binary;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Sega.Tests;

/// <summary>32X spec X1, X2 and X5 (box art).</summary>
public class Sega32xHandlerTests
{
    private static readonly ISystemHandler Handler = new Sega32xHandler();
    private static readonly SyntheticMegaDriveRomBuilder Builder = new() { Seed = 1701, Size = 524288, SystemType = "SEGA 32X" };

    [Test]
    public void Sega32x_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("s32x", "32X", "32X", "sega32x", "Sega - 32X")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".32x" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((16384L, 4194304L)));
    }

    [Test]
    public void ReadHeader_ReadsTheMegaDriveFields_ClassifiedS32x()
    {
        var header = Handler.ReadHeader(Builder.Build())!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("s32x", "TURBO COMET", true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?>
        {
            ["systemType"] = "SEGA 32X", ["copyright"] = "(C)SEGA 1991.JAN", ["domesticTitle"] = "TURBO KOMETTO",
            ["overseasTitle"] = "TURBO COMET", ["serial"] = "GM 00001234-00", ["checksum"] = "ABCD", ["region"] = "JUE",
        }));
    }

    [Test]
    public void ReadHeader_MegaDriveSystemType_IsStillClassifiedS32x() =>
        Assert.That(Handler.ReadHeader((Builder with { SystemType = "SEGA GENESIS" }).Build())!.ClassifiedSystemId, Is.EqualTo("s32x"));

    [Test]
    public void ReadHeader_NoSegaOrTooShort_IsNull()
    {
        Assert.That(Handler.ReadHeader((Builder with { SystemType = "32X" }).Build()), Is.Null);
        Assert.That(Handler.ReadHeader(Builder.Build().AsSpan(0, 0x1FF)), Is.Null);
        Assert.That(Handler.ReadHeader(ReadOnlySpan<byte>.Empty), Is.Null);
    }

    [Test]
    public void NormalizeForHashing_ReturnsTheInputUnchanged()
    {
        var rom = Builder.Build();
        Assert.That(Handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_DifferentFromMegaDrives()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new MegaDriveHandler().DefaultBoxArtPng.ToArray()));
    }
}
