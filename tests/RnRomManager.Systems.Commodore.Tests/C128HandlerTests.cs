using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore.Tests;

/// <summary>Commodore 128 B1–B7: the handler contract, the reused C64 container headers and its box art.</summary>
public class C128HandlerTests
{
    private static readonly ISystemHandler Handler = new C128Handler();

    [Test]
    public void C128_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("c128", "Commodore 128", "C128", "c128", "Commodore - 128")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".d64", ".d71", ".d81", ".g64", ".t64", ".tap", ".prg", ".p00", ".crt" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((256L, 8L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.DatSource), Is.EqualTo((true, DatSource.None)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That((Handler.FileSets, Handler.LegacyEmbeddedRoms, Handler.KeepsSourceExtension), Is.EqualTo((false, false, true)));
        Assert.That(Handler.BrowserCore, Is.EqualTo("vice_x128"));
    }

    [Test]
    public void ReadHeader_ReadsTheC64ContainerHeaders_AsC128()
    {
        var crt = new byte[64];
        Encoding.ASCII.GetBytes("C64 CARTRIDGE   ").CopyTo(crt, 0);
        crt[0x14] = 1;
        crt[0x18] = 1;
        Encoding.ASCII.GetBytes("PIXEL PILOT").CopyTo(crt, 0x20);
        var tap = new byte[20];
        Encoding.ASCII.GetBytes("C64-TAPE-RAW").CopyTo(tap, 0);
        tap[0x0C] = 1;

        var crtHeader = Handler.ReadHeader(crt)!;
        Assert.That(crtHeader.ClassifiedSystemId, Is.EqualTo("c128"));
        Assert.That(crtHeader.Fields, Is.EquivalentTo(new C64Handler().ReadHeader(crt)!.Fields), "C64's fields");
        Assert.That(crtHeader.Fields["format"], Is.EqualTo("crt"));

        var tapHeader = Handler.ReadHeader(tap)!;
        Assert.That((tapHeader.ClassifiedSystemId, tapHeader.Fields["format"]), Is.EqualTo(("c128", (object?)"tap")));
    }

    [Test]
    public void ReadHeader_IsNullForOtherFiles()
    {
        byte[] prg = [0x01, 0x1C, .. new byte[298]];
        var nativeCrt = new byte[64];
        Encoding.ASCII.GetBytes("C128 CARTRIDGE  ").CopyTo(nativeCrt, 0);

        Assert.That(Handler.ReadHeader(prg), Is.Null);
        Assert.That(Handler.ReadHeader(new byte[174_848]), Is.Null, ".d64");
        Assert.That(Handler.ReadHeader(nativeCrt), Is.Null, "a native C128 .crt");
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }
}
