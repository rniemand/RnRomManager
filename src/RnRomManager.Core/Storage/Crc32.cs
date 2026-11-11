namespace RnRomManager.Core.Storage;

/// <summary>CRC-32 (IEEE 802.3, reflected, as in ZIP and the DATs), lowercase hex like every hash in the app (spec §6.2).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>The running state to start <see cref="Update"/> from.</summary>
    public const uint Initial = 0xFFFFFFFFu;

    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(Initial, data));

    /// <summary>Feeds <paramref name="data"/> into a running state (start at <see cref="Initial"/>, end with <see cref="Finish"/>).</summary>
    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            state = Table[(state ^ b) & 0xFF] ^ (state >> 8);
        return state;
    }

    /// <summary>The CRC of everything fed into <paramref name="state"/>.</summary>
    public static uint Finish(uint state) => ~state;

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
