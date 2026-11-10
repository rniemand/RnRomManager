using System.Text;

namespace RnRomManager.TestData;

/// <summary>
/// Deterministic, non-copyrighted Mega Drive images for tests and seed sets: seeded random bytes with the header at
/// <c>0x100</c>. Each text field is padded with spaces to its width and written as Latin-1, so a test can set any byte
/// <c>0x00–0xFF</c>. Same properties → same bytes.
/// </summary>
public sealed record SyntheticMegaDriveRomBuilder
{
    public int Seed { get; init; }

    /// <summary>At least <c>0x200</c> bytes.</summary>
    public int Size { get; init; } = 524288;

    public string SystemType { get; init; } = "SEGA GENESIS";
    public string Copyright { get; init; } = "(C)SEGA 1991.JAN";
    public string DomesticTitle { get; init; } = "TURBO KOMETTO";
    public string OverseasTitle { get; init; } = "TURBO COMET";
    public string Serial { get; init; } = "GM 00001234-00";
    public ushort Checksum { get; init; } = 0xABCD;
    public string Region { get; init; } = "JUE";

    public byte[] Build()
    {
        if (Size < 0x200)
            throw new ArgumentException($"Size {Size} is under 0x200.", nameof(Size));
        var image = new byte[Size];
        // System.Random with a seed uses the legacy, fixed algorithm, so the bytes are stable across runtimes.
        new Random(Seed).NextBytes(image);
        Write(image, 0x100, 16, SystemType);
        Write(image, 0x110, 16, Copyright);
        Write(image, 0x120, 48, DomesticTitle);
        Write(image, 0x150, 48, OverseasTitle);
        Write(image, 0x180, 14, Serial);
        image[0x18E] = (byte)(Checksum >> 8);
        image[0x18F] = (byte)Checksum;
        Write(image, 0x1F0, 3, Region);
        return image;
    }

    private static void Write(byte[] image, int offset, int width, string value)
    {
        if (value.Length > width)
            throw new ArgumentException($"'{value}' is over {width} characters.", nameof(value));
        Encoding.Latin1.GetBytes(value.PadRight(width)).CopyTo(image, offset);
    }
}
