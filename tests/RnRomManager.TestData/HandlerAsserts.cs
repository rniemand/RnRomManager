using System.Buffers.Binary;
using NUnit.Framework;
using RnRomManager.Core.Systems;

namespace RnRomManager.TestData;

/// <summary>Assertions every <c>*HandlerTests</c> class shares (D5).</summary>
public static class HandlerAsserts
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The default box art is a PNG of <paramref name="size"/>×<paramref name="size"/> (read from its IHDR).</summary>
    public static void AssertBoxArtPng(ISystemHandler handler, int size)
    {
        var png = handler.DefaultBoxArtPng.Span;

        Assert.That(png[..8].ToArray(), Is.EqualTo(PngSignature), "PNG signature");
        Assert.That(System.Text.Encoding.ASCII.GetString(png[12..16]), Is.EqualTo("IHDR"));
        Assert.That((BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])), Is.EqualTo((size, size)));
    }

    public static void AssertNoHeader(ISystemHandler handler, byte[] rom) =>
        Assert.That(handler.ReadHeader(rom), Is.Null);

    public static void AssertNormalizeIsIdentity(ISystemHandler handler, byte[] rom) =>
        Assert.That(handler.NormalizeForHashing(rom).ToArray(), Is.EqualTo(rom));
}
