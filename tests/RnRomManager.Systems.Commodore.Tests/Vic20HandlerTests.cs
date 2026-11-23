using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

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
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".a0", ".20", ".40", ".60", ".bin", ".prg", ".d64", ".tap", ".t64", ".crt", ".b0", ".rom", ".d71", ".d81", ".g64", ".x64", ".p00" }));
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
        Assert.That(Handler.ReadHeader(cart), Is.Null);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }
}
