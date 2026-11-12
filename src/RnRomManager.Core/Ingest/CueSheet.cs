using System.Text;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Ingest;

/// <summary>
/// B8: the file names a <c>.cue</c> sheet's <c>FILE</c> lines name, in order, without repeats (ignoring case; the first
/// spelling is kept). Other cue commands are ignored.
/// </summary>
/// <param name="Files">The names; empty when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the cue can't be used, or null.</param>
public sealed record CueSheet(IReadOnlyList<string> Files, string? Error)
{
    /// <summary>A disc has at most 99 tracks, and each <c>FILE</c> holds at least one.</summary>
    public const int MaxFiles = 99;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public bool IsValid => Error is null;

    /// <summary>
    /// Reads <c>FILE "&lt;name&gt;" &lt;type&gt;</c> and unquoted <c>FILE &lt;name&gt; &lt;type&gt;</c> lines, ignoring case
    /// and leading whitespace. The bytes are UTF-8 (a leading BOM is dropped), or Latin-1 when they aren't valid UTF-8.
    /// </summary>
    public static CueSheet Parse(ReadOnlySpan<byte> bytes)
    {
        var text = Decode(bytes);
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var line = raw.TrimStart();
            if (line.Length < 5 || !line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase) || !char.IsWhiteSpace(line[4]))
                continue;

            var rest = line[5..].TrimStart();
            string name;
            if (rest.StartsWith('"'))
            {
                var close = rest.IndexOf('"', 1);
                if (close < 0)
                    return Fail($"a FILE line has no closing quote: {line}");
                name = rest[1..close];
            }
            else
            {
                var end = rest.IndexOfAny([' ', '\t']);
                name = end < 0 ? rest : rest[..end];
            }

            if (NameProblem(name) is { } problem)
                return Fail($"FILE name '{name}' {problem}");
            if (seen.Add(name))
                files.Add(name);
            if (files.Count > MaxFiles)
                return Fail($"more than {MaxFiles} distinct FILE names");
        }

        return files.Count == 0 ? Fail("no FILE line") : new CueSheet(files, null);
    }

    /// <summary>B8: only a name equal to <c>.</c> or <c>..</c>, or one with a separator, is refused for dots; <c>...Iru!</c> is fine.</summary>
    private static string? NameProblem(string name)
    {
        if (name.Length == 0)
            return "is empty";
        if (name is "." or "..")
            return "is not a file name";
        if (name.IndexOfAny(['/', '\\', ':']) >= 0)
            return "must not contain / \\ or :";
        return InfoJsonValidator.PortableNameProblem(name);
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            bytes = bytes[3..];
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static CueSheet Fail(string error) => new([], error);
}
