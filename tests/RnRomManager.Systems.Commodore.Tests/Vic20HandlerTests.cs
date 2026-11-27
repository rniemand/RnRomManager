using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Systems.Commodore.Tests;

/// <summary>Commodore VIC-20: the handler contract and its box art.</summary>
public class Vic20HandlerTests
{
    private static readonly ISystemHandler Handler = new Vic20Handler();

    [Test]
    public void Vic20_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("vic20", "Commodore VIC-20", "VIC-20", "vic20", "Commodore - VIC-20")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".a0", ".20", ".40", ".60", ".bin", ".prg", ".d64", ".tap", ".t64", ".crt", ".b0", ".rom", ".d71", ".d81", ".g64", ".x64", ".p00", ".d6z", ".d7z", ".d8z", ".g6z", ".x6z", ".gz", ".g41", ".g4z" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((256L, 8L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.DatSource, Handler.DatFolder), Is.EqualTo((true, DatSource.Libretro, DatKind.NoIntro)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That((Handler.FileSets, Handler.LegacyEmbeddedRoms, Handler.KeepsSourceExtension), Is.EqualTo((false, false, true)));
        Assert.That(Handler.BrowserCore, Is.EqualTo("vice_xvic"));
    }

    [Test]
    public void ReadHeader_IsAlwaysNull()
    {
        byte[] cart = [0x00, 0xA0, .. new byte[8190]]; // an autostart cartridge mapped at $A000
        HandlerAsserts.AssertNoHeader(Handler, cart);
    }

    [TestCase("Game.d6z", ".d64")]
    [TestCase("Game.d7z", ".d71")]
    [TestCase("Game.d8z", ".d81")]
    [TestCase("Game.g6z", ".g64")]
    [TestCase("Game.g4z", ".g41")]
    [TestCase("Game.g41.gz", ".g41")]
    [TestCase("Game.x6z", ".x64")]
    [TestCase("Game.D6Z", ".d64")]
    [TestCase("Game.d64.gz", ".d64")]
    [TestCase("Game.D64.GZ", ".d64")]
    [TestCase("Game.zzz.gz", null)]
    [TestCase("Game.d6z.gz", null)]
    [TestCase("Game.gz", null)]
    [TestCase("Game.PRG", ".prg")]
    public void SourceExtension_NamesTheImageAGzipHolds(string fileName, string? expected) =>
        Assert.That(Handler.SourceExtension(fileName), Is.EqualTo(expected));

    [Test]
    public void NormalizeForHashing_InflatesAGzip_AndKeepsAPlainImage()
    {
        var disk = new byte[174_848];
        new Random(64).NextBytes(disk);
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(disk);

        Assert.That(Handler.NormalizeForHashing(output.ToArray()).ToArray(), Is.EqualTo(disk));
        HandlerAsserts.AssertNormalizeIsIdentity(Handler, disk);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        HandlerAsserts.AssertBoxArtPng(Handler, 512);
    }
}
