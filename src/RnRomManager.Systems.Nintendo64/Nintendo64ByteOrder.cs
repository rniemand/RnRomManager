namespace RnRomManager.Systems.Nintendo64;

/// <summary>
/// Converts an N64 dump to big-endian (<c>.z64</c>) order, the order the No-Intro DAT hashes (N3). The order is read
/// from where <c>0x80</c> sits in the first word (the PI config word starts with it in big-endian), never from the
/// extension.
/// </summary>
public static class Nintendo64ByteOrder
{
    /// <returns>
    /// <paramref name="rom"/> itself when it is big-endian or its order is unknown (too short, no <c>0x80</c>, or a
    /// length the swap can't cover); otherwise a new, converted array. The input is never changed.
    /// </returns>
    public static byte[] ToBigEndian(byte[] rom)
    {
        if (rom.Length < 4 || rom[0] == 0x80)
            return rom;
        if (rom[1] == 0x80 && rom.Length % 2 == 0)
            return Swap(rom, 2);
        if (rom[3] == 0x80 && rom.Length % 4 == 0)
            return Swap(rom, 4);
        return rom;
    }

    /// <summary>Reverses every <paramref name="width"/>-byte group (2 = byte-swapped, 4 = little-endian).</summary>
    private static byte[] Swap(byte[] rom, int width)
    {
        var result = new byte[rom.Length];
        for (var i = 0; i < rom.Length; i += width)
            for (var j = 0; j < width; j++)
                result[i + j] = rom[i + width - 1 - j];
        return result;
    }
}
