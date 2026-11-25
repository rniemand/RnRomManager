using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Commodore.Tests;

/// <summary>Commodore Plus/4 B1–B7: the handler contract, the reused C64 header (with VICE's C16 tape) and its box art.</summary>
public class Plus4HandlerTests
{
    private static readonly ISystemHandler Handler = new Plus4Handler();

    [Test]
    public void Plus4_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("plus4", "Commodore Plus/4", "Plus/4", "plus4", "Commodore - Plus-4")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".d64", ".t64", ".tap", ".prg", ".crt", ".bin" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((256L, 8L * 1024 * 1024)));
        Assert.That((Handler.EmbedRomsInZip, Handler.DatSource, Handler.DatFolder), Is.EqualTo((true, DatSource.Libretro, DatKind.NoIntro)));
        Assert.That(Handler.MetadatKinds, Is.Empty);
        Assert.That((Handler.FileSets, Handler.LegacyEmbeddedRoms, Handler.KeepsSourceExtension), Is.EqualTo((false, false, true)));
        Assert.That(Handler.BrowserCore, Is.EqualTo("vice_xplus4"));
    }

    [Test]
    public void ReadHeader_ReadsAPlus4Tape()
    {
        var tap = new byte[20];
        Encoding.ASCII.GetBytes("C16-TAPE-RAW").CopyTo(tap, 0);
        tap[0x0C] = 1;

        var header = Handler.ReadHeader(tap)!;

        Assert.That((header.ClassifiedSystemId, header.IsPlausible), Is.EqualTo(("plus4", true)));
        Assert.That((header.Fields["format"], header.Fields["version"]), Is.EqualTo(((object?)"tap", (object?)1)));
    }

    [Test]
    public void ReadHeader_IsNullForOtherFiles()
    {
        byte[] prg = [0x01, 0x10, .. new byte[298]];
        var bin = new byte[16 * 1024];
        new Random(16).NextBytes(bin);
        var nativeCrt = new byte[64];
        Encoding.ASCII.GetBytes("C264 CARTRIDGE  ").CopyTo(nativeCrt, 0);

        Assert.That(Handler.ReadHeader(prg), Is.Null, ".prg");
        Assert.That(Handler.ReadHeader(new byte[174_848]), Is.Null, ".d64");
        Assert.That(Handler.ReadHeader(bin), Is.Null, ".bin cart");
        Assert.That(Handler.ReadHeader(nativeCrt), Is.Null, "a native Plus/4 .crt");
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
    }
}
