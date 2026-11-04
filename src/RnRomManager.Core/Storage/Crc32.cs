namespace RnRomManager.Core.Storage;

/// <summary>CRC-32 (IEEE 802.3, reflected, as in ZIP and the DATs), lowercase hex like every hash in the app (spec §6.2).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>8 lowercase hex chars.</summary>
    public static string ToHex(ReadOnlySpan<byte> data) => Compute(data).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
