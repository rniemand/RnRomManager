using System.Buffers.Binary;

namespace RnRomManager.Core.Library;

/// <summary>JPEG dimensions from the first start-of-frame marker (baseline, progressive, lossless, arithmetic).</summary>
public sealed record JpegInfo(int Width, int Height)
{
    /// <returns>null when it isn't a JPEG within <see cref="ImageFormats.MaxBytes"/> with a readable SOF segment.</returns>
    public static JpegInfo? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ImageFormats.MaxBytes || bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            return null;

        var i = 2;
        while (i + 4 <= bytes.Length)
        {
            if (bytes[i] != 0xFF)
                return null;
            var marker = bytes[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte
                continue;
            }
            // Standalone markers carry no length.
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                i += 2;
                continue;
            }
            if (marker is 0xD9 or 0xDA)
                return null; // end of image / start of scan before any frame header
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 2)..]);
            if (length < 2 || i + 2 + length > bytes.Length)
                return null;
            // SOF0–SOF15 except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 7)
                    return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 5)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 7)..]);
                return width == 0 || height == 0 ? null : new JpegInfo(width, height);
            }
            i += 2 + length;
        }
        return null;
    }
}
