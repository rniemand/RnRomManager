using System.Text;

namespace RnRomManager.Core.Naming;

/// <summary>Spec §6.1 Sanitize: a game title or ROM name made safe as a file name on every OS.</summary>
public static class FileNameSanitizer
{
    public const int MaxLength = 180;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly HashSet<string> ReservedAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONIN$", "CONOUT$",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    /// <summary>
    /// True when Windows treats <paramref name="name"/> as a DOS device: the part before the first dot, without
    /// trailing spaces, is a reserved name in any case. Besides the names <see cref="Sanitize"/> prefixes, this
    /// covers <c>CONIN$</c>, <c>CONOUT$</c> and the superscript-digit forms <c>COM¹</c>–<c>COM³</c> and
    /// <c>LPT¹</c>–<c>LPT³</c>. <see cref="Sanitize"/> keeps its own, smaller set, so existing names don't change.
    /// </summary>
    public static bool IsReservedDeviceName(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return Reserved.Contains(stem) || ReservedAliases.Contains(stem);
    }

    /// <summary>
    /// NFC; <c>\ / : * ? " &lt; &gt; |</c> and control chars → <c>_</c>; at most 180 chars; spaces trimmed and trailing
    /// dots removed; a Windows reserved name gets a <c>_</c> prefix. May return "" (callers reject that, §6.1).
    /// </summary>
    public static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Normalize(NormalizationForm.FormC));
        for (var i = 0; i < builder.Length; i++)
        {
            if (builder[i] is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(builder[i]))
                builder[i] = '_';
        }

        var result = builder.ToString();
        if (result.Length > MaxLength)
            result = result[..(char.IsHighSurrogate(result[MaxLength - 1]) ? MaxLength - 1 : MaxLength)];
        result = result.Trim(' ').TrimEnd('.', ' ');

        // Windows also reserves "CON.txt", so the part before the first dot decides.
        var dot = result.IndexOf('.', StringComparison.Ordinal);
        return Reserved.Contains(dot < 0 ? result : result[..dot]) ? "_" + result : result;
    }
}
