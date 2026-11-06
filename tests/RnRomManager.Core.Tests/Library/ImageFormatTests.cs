using RnRomManager.Core.Library;

namespace RnRomManager.Core.Tests.Library;

public class ImageFormatTests
{
    /// <summary>SOI, an APP0 segment, a fill byte, a frame header with <paramref name="sof"/>, then SOS.</summary>
    internal static byte[] Jpeg(byte sof, int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, // fill byte before the next marker
        0xFF, sof, 0x00, 0x11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
        1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        0xFF, 0xDA, 0x00, 0x02,
    ];

    internal static byte[] Png(int width, int height) =>
    [
        0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
        0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0, 0, (byte)(width >> 8), (byte)width, 0, 0, (byte)(height >> 8), (byte)height, 8, 6, 0, 0, 0,
    ];

    [TestCase((byte)0xC0, TestName = "Baseline")]
    [TestCase((byte)0xC2, TestName = "Progressive")]
    public void Jpeg_ReadsTheFrameSize(byte sof)
    {
        Assert.That(JpegInfo.Read(Jpeg(sof, 640, 480)), Is.EqualTo(new JpegInfo(640, 480)));
    }

    [Test]
    public void Jpeg_TruncatedOrWithoutFrame_IsNull()
    {
        var full = Jpeg(0xC0, 640, 480);
        Assert.That(JpegInfo.Read(full[..30]), Is.Null, "cut inside the frame header");
        Assert.That(JpegInfo.Read([0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02]), Is.Null, "scan before any frame");
        Assert.That(JpegInfo.Read([0xFF, 0xD8]), Is.Null);
        Assert.That(JpegInfo.Read(Png(1, 1)), Is.Null);
    }

    [Test]
    public void Detect_UsesMagicBytes()
    {
        Assert.That(ImageFormats.Detect(Png(1, 1)), Is.EqualTo(ImageFormat.Png));
        Assert.That(ImageFormats.Detect(Jpeg(0xC0, 1, 1)), Is.EqualTo(ImageFormat.Jpeg));
        Assert.That(ImageFormats.Detect("GIF89a"u8), Is.Null);
        Assert.That(ImageFormats.Detect([]), Is.Null);
    }

    [Test]
    public void Read_GivesFormatAndSize_OrNullForUnreadableOrTooLarge()
    {
        Assert.That(ImageFormats.Read(Png(300, 400)), Is.EqualTo((ImageFormat.Png, 300, 400)));
        Assert.That(ImageFormats.Read(Jpeg(0xC2, 64, 32)), Is.EqualTo((ImageFormat.Jpeg, 64, 32)));
        Assert.That(ImageFormats.Read([0xFF, 0xD8, 0xFF, 0xD9]), Is.Null, "JPEG magic but no size");
        var huge = new byte[ImageFormats.MaxBytes + 1];
        Png(1, 1).CopyTo(huge, 0);
        Assert.That(ImageFormats.Read(huge), Is.Null);
    }

    [TestCase("boxart-01.png", "image/png")]
    [TestCase("snap-02.JPG", "image/jpeg")]
    [TestCase("x.jpeg", "image/jpeg")]
    public void ContentType_ByExtension(string name, string expected) =>
        Assert.That(ImageFormats.ContentType(name), Is.EqualTo(expected));

    [Test]
    public void Limits_Stored8MiB_Thumbnails5MiB()
    {
        Assert.That(PngInfo.MaxBytes, Is.EqualTo(8 * 1024 * 1024));
        Assert.That(RnRomManager.Core.Media.MediaService.MaxThumbnailBytes, Is.EqualTo(5 * 1024 * 1024));
    }
}
