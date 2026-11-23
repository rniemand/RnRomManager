using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore.Tests;

/// <summary>Commodore PET: the handler contract and its box art.</summary>
public class PetHandlerTests
{
    private static readonly ISystemHandler Handler = new PetHandler();

    [Test]
    public void Pet_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("pet", "Commodore PET", "PET", "pet", "Commodore - PET")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".prg", ".d64", ".tap", ".t64", ".g64", ".d80", ".d82", ".d71", ".d81", ".x64", ".p00" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((256L, 8L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.DatSource, Handler.DatFolder), Is.EqualTo((true, DatSource.Libretro, DatKind.Tosec)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That((Handler.FileSets, Handler.LegacyEmbeddedRoms, Handler.KeepsSourceExtension), Is.EqualTo((false, false, true)));
        Assert.That(Handler.BrowserCore, Is.EqualTo("vice_xpet"));
    }

    [Test]
    public void ReadHeader_IsAlwaysNull()
    {
        byte[] prg = [0x01, 0x04, .. new byte[1022]]; // a BASIC program loading at $0401
        Assert.That(Handler.ReadHeader(prg), Is.Null);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }
}
