using System.IO.Compression;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Commodore.Tests;

/// <summary>Commodore Amiga: the handler contract and its box art.</summary>
public class AmigaHandlerTests
{
    private static readonly ISystemHandler Handler = new AmigaHandler();

    [Test]
    public void Amiga_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("amiga", "Commodore Amiga", "Amiga", "amiga", "Commodore - Amiga")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".adf", ".adz", ".ipf" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((1024L, 4L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.DatSource, Handler.DatFolder), Is.EqualTo((true, DatSource.Libretro, DatKind.Tosec)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That((Handler.FileSets, Handler.LegacyEmbeddedRoms, Handler.KeepsSourceExtension), Is.EqualTo((false, false, false)));
        Assert.That(Handler.BrowserCore, Is.EqualTo("puae"));
    }

    [TestCase(".ipf", true)]
    [TestCase(".IPF", true)]
    [TestCase(".adf", false)]
    [TestCase(".adz", false)]
    public void Review_KeepsOnlyAnIpfsExtension(string extension, bool keeps) =>
        Assert.That(Handler.KeepsExtension(extension), Is.EqualTo(keeps));

    [Test]
    public void ReadHeader_IsAlwaysNull()
    {
        byte[] disk = [(byte)'D', (byte)'O', (byte)'S', 0, .. new byte[901_116]]; // a DD AmigaDOS boot block
        HandlerAsserts.AssertNoHeader(Handler, disk);
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(bytes);
        return output.ToArray();
    }

    [Test]
    public void NormalizeForHashing_InflatesAnAdz_AndKeepsAnAdf()
    {
        byte[] disk = [(byte)'D', (byte)'O', (byte)'S', 0, .. new byte[901_116]];

        Assert.That(Handler.NormalizeForHashing(Gzip(disk)).ToArray(), Is.EqualTo(disk));
        HandlerAsserts.AssertNormalizeIsIdentity(Handler, disk);
    }

    [Test]
    public void NormalizeForHashing_KeepsACorruptOrOversizedGzip()
    {
        byte[] corrupt = [0x1F, 0x8B, 0x08, 0x00, 1, 2, 3, 4, 5, 6, 7, 8];
        var bomb = Gzip(new byte[4 * 1024 * 1024 + 1]);

        HandlerAsserts.AssertNormalizeIsIdentity(Handler, corrupt);
        HandlerAsserts.AssertNormalizeIsIdentity(Handler, bomb);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        HandlerAsserts.AssertBoxArtPng(Handler, 512);
    }
}
