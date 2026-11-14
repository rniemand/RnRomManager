using System.Globalization;
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
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(Decode(bytes)))
        {
            if (!IsFileLine(line))
                continue;
            if (ReadFileName(line, out var name, out _) is { } error)
                return Fail(error);
            if (seen.Add(name))
                files.Add(name);
            if (files.Count > MaxFiles)
                return Fail($"more than {MaxFiles} distinct FILE names");
        }

        return files.Count == 0 ? Fail("no FILE line") : new CueSheet(files, null);
    }

    /// <summary>B8: only a name equal to <c>.</c> or <c>..</c>, or one with a separator, is refused for dots; <c>...Iru!</c> is fine.</summary>
    internal static string? NameProblem(string name)
    {
        if (name.Length == 0)
            return "is empty";
        if (name is "." or "..")
            return "is not a file name";
        if (name.IndexOfAny(['/', '\\', ':']) >= 0)
            return "must not contain / \\ or :";
        return InfoJsonValidator.PortableNameProblem(name);
    }

    /// <summary>
    /// Play B2: a playback-only copy of a stored cue for the pinned PCSX ReARMed parser, which matches keywords case
    /// sensitively, splits on ASCII spaces, reads 256-byte lines and lets every <c>INDEX</c> overwrite the track start.
    /// The copy is named after <paramref name="cueFileName"/> with a lowercase <c>.cue</c>, names its tracks
    /// <c>track-01.bin</c>… in FILE order, and keeps only FILE, TRACK, INDEX and PREGAP in canonical form (uppercase,
    /// single spaces, UTF-8 without BOM, LF). Its FILE names must cover <paramref name="storedTracks"/> one to one,
    /// ignoring case. The stored cue is never changed.
    /// </summary>
    /// <param name="bytes">The stored cue, at most <see cref="IngestSetPlanner.MaxCueBytes"/>.</param>
    /// <param name="cueFileName">The stored cue's file name, ending in <c>.cue</c> (any case).</param>
    /// <param name="storedTracks">The release's stored track file names (the cue excluded).</param>
    public static PlaybackCue ForPlayback(ReadOnlySpan<byte> bytes, string cueFileName, IReadOnlyCollection<string> storedTracks)
    {
        if (bytes.Length > IngestSetPlanner.MaxCueBytes)
            return PlaybackCue.Fail($"the cue is larger than {IngestSetPlanner.MaxCueBytes} bytes");
        if (!cueFileName.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) || cueFileName.Length == 4 || NameProblem(cueFileName) is not null)
            return PlaybackCue.Fail($"'{cueFileName}' is not a usable cue file name");

        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in storedTracks)
            if (NameProblem(track) is not null || !stored.TryAdd(track, track))
                return PlaybackCue.Fail($"stored track '{track}' is not a usable, case-insensitively unique file name");

        var output = new List<string>();
        var tracks = new List<PlaybackTrack>();
        var fileTracks = 0;           // TRACKs under the current FILE
        var trackNumber = 0;          // the last TRACK's number
        var index00 = false;          // the current TRACK's INDEX 00, INDEX 01 and PREGAP
        var index01 = true;
        var pregap = false;
        foreach (var line in Lines(Decode(bytes)))
        {
            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;
            var keyword = tokens[0].ToUpperInvariant();
            switch (keyword)
            {
                case "FILE":
                {
                    if (tokens.Length < 2)
                        return PlaybackCue.Fail("a FILE line has no name");
                    if (ReadFileName(line, out var name, out var rest) is { } error)
                        return PlaybackCue.Fail(error);
                    if (rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries) is not [var type] || !type.Equals("BINARY", StringComparison.OrdinalIgnoreCase))
                        return PlaybackCue.Fail($"FILE '{name}' must be followed by BINARY only");
                    if (tracks.Count > 0 && fileTracks == 0)
                        return PlaybackCue.Fail($"FILE '{tracks[^1].StoredName}' has no TRACK");
                    if (!index01)
                        return PlaybackCue.Fail($"TRACK {trackNumber:00} has no INDEX 01");
                    if (!stored.TryGetValue(name, out var storedName))
                        return PlaybackCue.Fail($"FILE '{name}' is not a stored track of this release");
                    if (tracks.Any(t => t.StoredName == storedName))
                        return PlaybackCue.Fail($"FILE '{name}' is repeated");
                    if (tracks.Count == MaxFiles)
                        return PlaybackCue.Fail($"more than {MaxFiles} FILE blocks");
                    var alias = $"track-{tracks.Count + 1:00}.bin";
                    tracks.Add(new PlaybackTrack(storedName, alias));
                    output.Add($"FILE \"{alias}\" BINARY");
                    fileTracks = 0;
                    break;
                }
                case "TRACK":
                {
                    if (tracks.Count == 0)
                        return PlaybackCue.Fail("TRACK before the first FILE");
                    if (tokens.Length != 3 || !TryTwoDigits(tokens[1], out var number))
                        return PlaybackCue.Fail($"malformed TRACK line: {line.Trim()}");
                    if (!index01)
                        return PlaybackCue.Fail($"TRACK {trackNumber:00} has no INDEX 01");
                    if (number != trackNumber + 1)
                        return PlaybackCue.Fail($"TRACK {number:00} is out of order: tracks must run 01, 02… without gaps");
                    var mode = tokens[2].ToUpperInvariant();
                    if (mode is not ("AUDIO" or "MODE1/2352" or "MODE2/2352"))
                        return PlaybackCue.Fail($"TRACK {number:00} mode '{tokens[2]}' is not AUDIO, MODE1/2352 or MODE2/2352");
                    trackNumber = number;
                    fileTracks++;
                    (index00, index01, pregap) = (false, false, false);
                    output.Add($"  TRACK {number:00} {mode}");
                    break;
                }
                case "INDEX":
                {
                    if (fileTracks == 0)
                        return PlaybackCue.Fail("INDEX outside a TRACK");
                    if (tokens.Length != 3 || !TryTwoDigits(tokens[1], out var index) || !TryPosition(tokens[2], out var position))
                        return PlaybackCue.Fail($"malformed INDEX line: {line.Trim()}");
                    // The pinned parser lets every INDEX overwrite the track start, so only 00 then 01 is safe.
                    if (index == 0 && !index00 && !index01)
                        index00 = true;
                    else if (index == 1 && !index01)
                        index01 = true;
                    else
                        return PlaybackCue.Fail($"TRACK {trackNumber:00} may only have INDEX 00 followed by one INDEX 01");
                    output.Add($"    INDEX {index:00} {position}");
                    break;
                }
                case "PREGAP":
                {
                    if (fileTracks == 0)
                        return PlaybackCue.Fail("PREGAP outside a TRACK");
                    if (tokens.Length != 2 || !TryPosition(tokens[1], out var length))
                        return PlaybackCue.Fail($"malformed PREGAP line: {line.Trim()}");
                    if (pregap || index01)
                        return PlaybackCue.Fail($"TRACK {trackNumber:00} may only have one PREGAP, before INDEX 01");
                    pregap = true;
                    output.Add($"    PREGAP {length}");
                    break;
                }
                // REM, TITLE, PERFORMER, FLAGS, POSTGAP, CATALOG and other metadata: the pinned parser reads none of it.
            }
        }

        if (tracks.Count == 0)
            return PlaybackCue.Fail("no FILE line");
        if (fileTracks == 0)
            return PlaybackCue.Fail($"FILE '{tracks[^1].StoredName}' has no TRACK");
        if (!index01)
            return PlaybackCue.Fail($"TRACK {trackNumber:00} has no INDEX 01");
        var unreferenced = stored.Values.Where(s => tracks.All(t => t.StoredName != s)).Order(StringComparer.Ordinal).ToList();
        if (unreferenced.Count > 0)
            return PlaybackCue.Fail($"the cue doesn't reference stored track(s): {string.Join(", ", unreferenced)}");
        if (output.FirstOrDefault(l => Encoding.UTF8.GetByteCount(l) > PlaybackCue.MaxLineBytes) is { } longLine)
            return PlaybackCue.Fail($"a playback line is longer than {PlaybackCue.MaxLineBytes} bytes: {longLine}");

        var content = Encoding.UTF8.GetBytes(string.Concat(output.Select(l => l + "\n")));
        return new PlaybackCue(cueFileName[..^4] + ".cue", content, tracks, null);
    }

    private static IEnumerable<string> Lines(string text) => text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    private static bool IsFileLine(string line)
    {
        line = line.TrimStart();
        return line.Length >= 5 && line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(line[4]);
    }

    /// <summary>The name of a FILE line (quoted, or up to the next space or tab) and the text after it; null or an error.</summary>
    private static string? ReadFileName(string line, out string name, out string rest)
    {
        line = line.TrimStart();
        var afterKeyword = line[5..].TrimStart();
        if (afterKeyword.StartsWith('"'))
        {
            var close = afterKeyword.IndexOf('"', 1);
            if (close < 0)
            {
                (name, rest) = ("", "");
                return $"a FILE line has no closing quote: {line}";
            }
            (name, rest) = (afterKeyword[1..close], afterKeyword[(close + 1)..]);
        }
        else
        {
            var end = afterKeyword.IndexOfAny([' ', '\t']);
            (name, rest) = end < 0 ? (afterKeyword, "") : (afterKeyword[..end], afterKeyword[end..]);
        }

        return NameProblem(name) is { } problem ? $"FILE name '{name}' {problem}" : null;
    }

    /// <summary>One or two ASCII digits, 1–99 for a TRACK, 0–99 for an INDEX.</summary>
    private static bool TryTwoDigits(string token, out int value)
    {
        value = 0;
        return token.Length is 1 or 2 && token.All(char.IsAsciiDigit) && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary><c>MM:SS:FF</c> (one or two digits each; seconds 00–59, frames 00–74), written back with two digits each.</summary>
    private static bool TryPosition(string token, out string position)
    {
        position = "";
        var parts = token.Split(':');
        if (parts.Length != 3 || !TryTwoDigits(parts[0], out var minutes) || !TryTwoDigits(parts[1], out var seconds)
            || !TryTwoDigits(parts[2], out var frames) || seconds > 59 || frames > 74)
            return false;
        position = $"{minutes:00}:{seconds:00}:{frames:00}";
        return true;
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

/// <summary>One stored track and the short name it has inside a playback archive.</summary>
public sealed record PlaybackTrack(string StoredName, string Alias);

/// <summary>Play B2: the result of <see cref="CueSheet.ForPlayback"/>.</summary>
/// <param name="CueName">The playback cue's file name: the stored cue's base name with <c>.cue</c>.</param>
/// <param name="Content">The canonical cue (UTF-8, no BOM, LF).</param>
/// <param name="Tracks">Each stored track and its alias, in FILE order; every stored track exactly once.</param>
/// <param name="Error">Why the cue can't be played, or null.</param>
public sealed record PlaybackCue(string CueName, byte[] Content, IReadOnlyList<PlaybackTrack> Tracks, string? Error)
{
    /// <summary>The pinned parser reads lines into a 256-byte buffer: 254 bytes, the LF and the terminator.</summary>
    public const int MaxLineBytes = 254;

    public bool IsValid => Error is null;

    internal static PlaybackCue Fail(string error) => new("", [], [], error);
}
