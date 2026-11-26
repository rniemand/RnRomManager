namespace RnRomManager.Core.Systems;

/// <summary>Decodes the ASCII text fields of ROM headers (titles, product and maker codes).</summary>
public static class HeaderText
{
    /// <summary>
    /// ASCII up to the first NUL, bytes outside 0x20–0x7E → <c>?</c>, then <see cref="string.Trim()"/> when
    /// <paramref name="trimStart"/> is true or trailing spaces only when false; null when nothing is left.
    /// </summary>
    public static string? ReadAscii(ReadOnlySpan<byte> bytes, bool trimStart)
    {
        var end = bytes.IndexOf((byte)0);
        var text = Printable(end < 0 ? bytes : bytes[..end]);
        text = trimStart ? text.Trim() : text.TrimEnd(' ');
        return text.Length > 0 ? text : null;
    }

    /// <summary>Every byte as a character: 0x20–0x7E kept, every other byte (NUL included) becomes <c>?</c>.</summary>
    public static string Printable(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            chars[i] = bytes[i] is >= 0x20 and <= 0x7E ? (char)bytes[i] : '?';
        return new string(chars);
    }
}
