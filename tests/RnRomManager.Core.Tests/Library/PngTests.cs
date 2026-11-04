using System.Buffers.Binary;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Spec §10.2 PNG validation.</summary>
public class PngTests
{
    /// <summary>Signature + IHDR (CRC not checked) + IEND: enough for <see cref="PngInfo"/>.</summary>
    public static byte[] Png(uint width, uint height)
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0, 0, 0, 0, 0, 0,
            8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "IEND"u8, 0xAE, 0x42, 0x60, 0x82];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    [Test]
    public void Read_ReturnsIhdrDimensions() =>
        Assert.That(PngInfo.Read(Png(512, 300)), Is.EqualTo(new PngInfo(512, 300)));

    private static IEnumerable<TestCaseData> Invalid()
    {
        yield return new TestCaseData(Array.Empty<byte>()).SetName("empty");
        yield return new TestCaseData(Png(1, 1)[..23]).SetName("truncated IHDR");
        yield return new TestCaseData((byte[])[0x88, .. Png(1, 1)[1..]]).SetName("bad signature");
        var notIhdr = Png(1, 1);
        notIhdr[12] = (byte)'X';
        yield return new TestCaseData(notIhdr).SetName("first chunk not IHDR");
        yield return new TestCaseData(Png(0, 1)).SetName("zero width");
        yield return new TestCaseData(Png(1, 0)).SetName("zero height");
        yield return new TestCaseData(Png(0x8000_0000, 1)).SetName("width over int.MaxValue");
        yield return new TestCaseData((byte[])[.. Png(1, 1), .. new byte[PngInfo.MaxBytes]]).SetName("over 5 MiB");
        yield return new TestCaseData("GIF89a......................"u8.ToArray()).SetName("not a PNG");
    }

    [TestCaseSource(nameof(Invalid))]
    public void Read_RejectsInvalid(byte[] bytes) => Assert.That(PngInfo.Read(bytes), Is.Null);

    [Test]
    public void Read_AcceptsExactly5MiB()
    {
        byte[] bytes = [.. Png(1, 1), .. new byte[PngInfo.MaxBytes - Png(1, 1).Length]];
        Assert.That(PngInfo.Read(bytes), Is.EqualTo(new PngInfo(1, 1)));
    }
}
