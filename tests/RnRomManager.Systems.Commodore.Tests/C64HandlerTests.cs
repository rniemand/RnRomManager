using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore.Tests;

/// <summary>C64 B1: the handler contract and its box art.</summary>
public class C64HandlerTests
{
    private static readonly ISystemHandler Handler = new C64Handler();

    [Test]
    public void C64_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("c64", "Commodore 64", "C64", "c64", "Commodore - 64")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".crt", ".tap", ".nib", ".d64", ".g64", ".t64", ".prg", ".bin", ".d71", ".d81", ".x64", ".p00", ".d80", ".d82", ".d6z", ".d7z", ".d8z", ".g6z", ".x6z", ".gz", ".nbz", ".d2m", ".d4m", ".tcrt" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((1024L, 8L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.DatSource, Handler.DatFolder), Is.EqualTo((true, DatSource.Libretro, DatKind.NoIntro)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That((Handler.FileSets, Handler.LegacyEmbeddedRoms, Handler.KeepsSourceExtension), Is.EqualTo((false, false, true)));
    }

    [Test]
    public void BrowserCore_IsVice() => Assert.That(Handler.BrowserCore, Is.EqualTo("vice_x64sc"));


    [TestCase("Game.d6z", ".d64")]
    [TestCase("Game.d7z", ".d71")]
    [TestCase("Game.d8z", ".d81")]
    [TestCase("Game.g6z", ".g64")]
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
        Assert.That(Handler.NormalizeForHashing(disk).ToArray(), Is.EqualTo(disk));
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }
}
