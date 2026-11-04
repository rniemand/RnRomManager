using System.Buffers.Binary;

namespace RnRomManager.Core.Library;

/// <summary>PNG validation for box art and snaps (spec §10.2): signature, ≤ 5 MiB, dimensions from IHDR.</summary>
public sealed record PngInfo(int Width, int Height)
{
    public const int MaxBytes = 5 * 1024 * 1024;

    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> IhdrHeader => [0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    /// <returns>null when it isn't a PNG we accept.</returns>
    public static PngInfo? Read(ReadOnlySpan<byte> bytes)
    {
        // Signature (8) + IHDR length/type (8) + width/height (8); IHDR must be the first chunk.
        if (bytes.Length > MaxBytes || bytes.Length < 24 || !bytes.StartsWith(Signature) || !bytes[8..16].SequenceEqual(IhdrHeader))
            return null;

        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
        return width is 0 or > int.MaxValue || height is 0 or > int.MaxValue ? null : new PngInfo((int)width, (int)height);
    }
}
