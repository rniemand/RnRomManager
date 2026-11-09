namespace RnRomManager.Systems.Snes.Tests;

/// <summary>SNES spec S3.</summary>
public class SnesCopierHeaderTests
{
    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

    [TestCase(1)]
    [TestCase(8)]
    public void SizeOf512Mod1024_LosesExactlyTheFirst512Bytes(int kib)
    {
        var rom = Bytes(kib * 1024 + 512);
        Assert.That(SnesCopierHeader.Strip(rom).ToArray(), Is.EqualTo(rom[512..]));
    }

    [TestCase(8 * 1024)]
    [TestCase(8 * 1024 + 256)]
    [TestCase(0)]
    public void OtherSizes_AreUnchanged(int length)
    {
        var rom = Bytes(length);
        Assert.That(SnesCopierHeader.Strip(rom).ToArray(), Is.EqualTo(rom));
    }
}
