using RnRomManager.TestData;

namespace RnRomManager.Systems.Nintendo64.Tests;

/// <summary>N64 spec N3. The vectors are written by hand, not by the builder.</summary>
public class Nintendo64ByteOrderTests
{
    private static readonly byte[] Z64 = [0x80, 0x37, 0x12, 0x40, 0xAA, 0xBB, 0xCC, 0xDD];
    private static readonly byte[] V64 = [0x37, 0x80, 0x40, 0x12, 0xBB, 0xAA, 0xDD, 0xCC];
    private static readonly byte[] N64 = [0x40, 0x12, 0x37, 0x80, 0xDD, 0xCC, 0xBB, 0xAA];

    [Test]
    public void BigEndian_IsReturnedAsIs()
    {
        var rom = (byte[])Z64.Clone();
        Assert.That(Nintendo64ByteOrder.ToBigEndian(rom), Is.SameAs(rom));
    }

    [Test]
    public void ByteSwapped_SwapsEveryPair() => Assert.That(Nintendo64ByteOrder.ToBigEndian(V64), Is.EqualTo(Z64));

    [Test]
    public void LittleEndian_ReversesEveryWord() => Assert.That(Nintendo64ByteOrder.ToBigEndian(N64), Is.EqualTo(Z64));

    [Test]
    public void TheOrderComesFromThe0x80Position_NotTheWholeWord()
    {
        byte[] z64 = [0x80, 0x27, 0x07, 0x40];
        Assert.That(Nintendo64ByteOrder.ToBigEndian([0x27, 0x80, 0x40, 0x07]), Is.EqualTo(z64));
        Assert.That(Nintendo64ByteOrder.ToBigEndian([0x40, 0x07, 0x27, 0x80]), Is.EqualTo(z64));
    }

    [TestCase(new byte[] { 0x12, 0x34, 0x56, 0x78 }, TestName = "Unknown marker")]
    [TestCase(new byte[] { 0x37, 0x80, 0x40 }, TestName = "Shorter than 4 bytes")]
    [TestCase(new byte[] { 0x37, 0x80, 0x40, 0x12, 0xBB }, TestName = "Odd-length byte-swapped")]
    [TestCase(new byte[] { 0x40, 0x12, 0x37, 0x80, 0xDD, 0xCC }, TestName = "Little-endian not a multiple of 4")]
    public void UnconvertibleInput_IsReturnedUnchanged(byte[] rom)
    {
        var copy = (byte[])rom.Clone();
        Assert.That(Nintendo64ByteOrder.ToBigEndian(rom), Is.SameAs(rom));
        Assert.That(rom, Is.EqualTo(copy));
    }

    [Test]
    public void Conversion_NeverMutatesTheInput()
    {
        var v64 = (byte[])V64.Clone();
        var n64 = (byte[])N64.Clone();
        Nintendo64ByteOrder.ToBigEndian(v64);
        Nintendo64ByteOrder.ToBigEndian(n64);
        Assert.That((v64, n64), Is.EqualTo((V64, N64)));
    }

    [TestCase(N64ByteOrder.V64)]
    [TestCase(N64ByteOrder.N64)]
    public void BuilderRoms_InEveryOrder_ConvertToTheirZ64Bytes(N64ByteOrder order)
    {
        var rom = new SyntheticN64RomBuilder { Seed = 3, Title = "ORDER TEST" };
        Assert.That(Nintendo64ByteOrder.ToBigEndian((rom with { ByteOrder = order }).Build()), Is.EqualTo(rom.Build()));
    }
}
