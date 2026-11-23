using System.Buffers.Binary;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;

namespace RnRomManager.Systems.Snes.Tests;

/// <summary>Sufami Turbo: the handler contract (spec B1), Snes9x's cart test as the header check (B3) and the box art (B5).</summary>
public class SufamiTurboHandlerTests
{
    private static readonly ISystemHandler Handler = new SufamiTurboHandler();

    /// <summary>A synthetic 512 KiB cart: random bytes after the <c>BANDAI SFC-ADX</c> magic.</summary>
    internal static byte[] Cart()
    {
        var cart = new byte[512 * 1024];
        new Random(7).NextBytes(cart);
        "BANDAI SFC-ADX"u8.CopyTo(cart);
        "STAR DUEL     "u8.CopyTo(cart.AsSpan(0x10));
        return cart;
    }

    [Test]
    public void SufamiTurbo_Contract()
    {
        Assert.That((Handler.Id, Handler.DisplayName, Handler.ShortName, Handler.Family, Handler.DatName),
            Is.EqualTo(("sufami", "Sufami Turbo", "ST", "sufami", "Nintendo - Sufami Turbo")));
        Assert.That(Handler.RomExtensions, Is.EqualTo(new[] { ".st" }));
        Assert.That((Handler.MinRomSizeBytes, Handler.MaxRomSizeBytes), Is.EqualTo((524_288L, 1_048_576L)));
        Assert.That((Handler.EmbedRomsInZip, Handler.DatSource, Handler.DatFolder), Is.EqualTo((true, DatSource.Libretro, DatKind.NoIntro)));
        Assert.That((Handler.FileSets, Handler.KeepsSourceExtension), Is.EqualTo((false, false)));
        Assert.That(Handler.MetadatKinds, Is.EqualTo(DatKind.Metadat));
        Assert.That(Handler.BrowserCore, Is.EqualTo("snes9x"));
    }

    [Test]
    public void ReadHeader_WithTheMagic_IsPlausible()
    {
        var header = Handler.ReadHeader(Cart())!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("sufami", (string?)null, true)));
        Assert.That(header.Fields, Is.EqualTo(new Dictionary<string, object?> { ["headerValid"] = true }));
    }

    [Test]
    public void ReadHeader_WithoutTheMagic_IsImplausible()
    {
        var cart = Cart();
        cart[0] = (byte)'X';

        var header = Handler.ReadHeader(cart)!;
        Assert.That((header.ClassifiedSystemId, header.IsPlausible, header.Fields["headerValid"]), Is.EqualTo(("sufami", false, (object?)false)));
    }

    [Test]
    public void ReadHeader_OfTheBiosMarker_IsImplausible()
    {
        var cart = Cart();
        "SFC-ADX BACKUP"u8.CopyTo(cart.AsSpan(0x10));

        Assert.That(Handler.ReadHeader(cart)!.IsPlausible, Is.False);
    }

    [Test]
    public void ReadHeader_OfAShortSpan_IsImplausible_WithoutThrowing()
    {
        Assert.That(Handler.ReadHeader([])!.IsPlausible, Is.False);
        Assert.That(Handler.ReadHeader("BANDAI SFC-ADX"u8)!.IsPlausible, Is.False);
    }

    [Test]
    public void DefaultBoxArt_IsA512x512Png_OfItsOwn()
    {
        var png = Handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((512, 512)));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new SnesHandler().DefaultBoxArtPng.ToArray()));
        Assert.That(png.ToArray(), Is.Not.EqualTo(new SatellaviewHandler().DefaultBoxArtPng.ToArray()));
    }
}
