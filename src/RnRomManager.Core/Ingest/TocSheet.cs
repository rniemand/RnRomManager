using System.Globalization;
using System.Text;

namespace RnRomManager.Core.Ingest;

/// <summary>
/// File type gaps D3–D5: the subset of a cdrdao <c>.toc</c> that rippers write. Decoded and split into lines as
/// <see cref="CueSheet"/> does; <c>//</c> starts a comment, strings take cdrdao's escapes, keywords match ignoring case,
/// and each statement is on one line. <c>CD_TEXT { … }</c> blocks are skipped.
/// </summary>
/// <param name="Files">The distinct file names, in first-use order (ignoring case); empty when <paramref name="Error"/> is set.</param>
/// <param name="Tracks">The tracks in order; empty when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the <c>.toc</c> can't be used, or null.</param>
public sealed record TocSheet(IReadOnlyList<string> Files, IReadOnlyList<TocTrack> Tracks, string? Error)
{
    internal static readonly HashSet<string> TrackModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUDIO", "MODE1", "MODE1_RAW", "MODE2", "MODE2_FORM1", "MODE2_FORM2", "MODE2_FORM_MIX", "MODE2_RAW",
    };

    internal static readonly HashSet<string> SubchannelModes = new(StringComparer.OrdinalIgnoreCase) { "RW", "RW_RAW" };

    public bool IsValid => Error is null;

    public static TocSheet Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > IngestSetPlanner.MaxCueBytes)
            return Fail($"the .toc is larger than {IngestSetPlanner.MaxCueBytes} bytes");
        var parser = new Parser(Tokenize(CueSheet.Decode(bytes)));
        if (parser.Run() is { } error)
            return Fail(error);

        var tracks = parser.Tracks;
        if (tracks.Count == 0)
            return Fail("the .toc has no TRACK");
        if (tracks.Count > 99)
            return Fail("the .toc has more than 99 tracks");
        if (tracks.FirstOrDefault(t => t.Items.Count == 0 && !t.HasSilence) is { } empty)
            return Fail($"track {empty.Number} has no data");
        var files = tracks.SelectMany(t => t.Items).Select(i => i.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
            return Fail("the .toc names no file");
        if (files.Count > CueSheet.MaxFiles)
            return Fail($"the .toc names more than {CueSheet.MaxFiles} files");
        return new TocSheet(files, tracks.Select(t => t.Build()).ToList(), null);
    }

    /// <summary>
    /// File type gaps D6: a playback cue for a stored TOC set. Each file the <c>.toc</c> names becomes
    /// <c>&lt;trackPrefix&gt;NN.bin</c>; only raw AUDIO/MODE1/MODE2 tracks with one unswapped data item each can be played.
    /// </summary>
    /// <param name="storedTracks">The set's other members, matched to <see cref="Files"/> ignoring case.</param>
    public static PlaybackCue ForPlayback(
        ReadOnlySpan<byte> toc,
        string tocFileName,
        IReadOnlyCollection<string> storedTracks,
        string trackPrefix)
    {
        var sheet = Parse(toc);
        if (sheet.Error is { } error)
            return PlaybackCue.Fail(error);
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in storedTracks)
            stored.TryAdd(track, track);
        if (sheet.Files.FirstOrDefault(f => !stored.ContainsKey(f)) is { } missing)
            return PlaybackCue.Fail($"'{missing}' isn't part of the release");
        var unreferenced = stored.Values.Where(s => !sheet.Files.Contains(s, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        if (unreferenced.Count > 0)
            return PlaybackCue.Fail($"the .toc doesn't reference stored track(s): {string.Join(", ", unreferenced)}");

        var cue = new StringBuilder();
        var tracks = new List<PlaybackTrack>();
        var finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? file = null;
        (TocTrack Track, int Position)? previous = null; // the previous track in the current file
        foreach (var track in sheet.Tracks)
        {
            var t = track.Number;
            if (track.Mode is not ("AUDIO" or "MODE1_RAW" or "MODE2_RAW"))
                return PlaybackCue.Fail($"track {t}: {track.Mode} can't be played; only AUDIO, MODE1_RAW and MODE2_RAW can");
            if (track.Subchannel is not null)
                return PlaybackCue.Fail($"track {t} stores subchannel data");
            if (track.HasSilence)
                return PlaybackCue.Fail($"track {t} uses SILENCE or ZERO");
            if (track.Items.Count > 1)
                return PlaybackCue.Fail($"track {t} has more than one FILE or DATAFILE");
            var item = track.Items[0];
            if (item.Swap)
                return PlaybackCue.Fail($"track {t} byte-swaps its audio (SWAP)");
            if (!item.FileName.Equals(file, StringComparison.OrdinalIgnoreCase))
            {
                if (!finished.Add(item.FileName))
                    return PlaybackCue.Fail($"'{item.FileName}' is used again after another file");
                file = item.FileName;
                previous = null;
                var alias = $"{trackPrefix}{tracks.Count + 1:00}.bin";
                tracks.Add(new PlaybackTrack(stored[file], alias));
                cue.Append($"FILE \"{alias}\" BINARY\n");
            }

            int? previousEnd = null;
            if (previous is var (before, at))
            {
                if (before.Items[0].Length is not { } length)
                    return PlaybackCue.Fail($"track {before.Number} has no length but isn't the last track of '{item.FileName}'");
                previousEnd = at + length;
            }

            if (item.Offset % SectorBytes is > 0)
                return PlaybackCue.Fail($"track {t}: #{item.Offset} isn't a whole number of {SectorBytes}-byte sectors");
            var sectors = (int?)(item.Offset / SectorBytes);
            var position = item.Kind == TocItemKind.File ? (sectors ?? 0) + item.Start!.Value : sectors ?? previousEnd ?? 0;
            if (position < previousEnd)
                return PlaybackCue.Fail($"track {t} starts before track {previous!.Value.Track.Number} ends in '{item.FileName}'");

            cue.Append($"  TRACK {t:00} {track.Mode switch { "AUDIO" => "AUDIO", "MODE1_RAW" => "MODE1/2352", _ => "MODE2/2352" }}\n");
            if (track.Pregap is { } pregap)
                cue.Append($"    PREGAP {CloneCdSheet.Time(pregap)}\n");
            if (track.Start is { } start)
                cue.Append($"    INDEX 00 {CloneCdSheet.Time(position)}\n    INDEX 01 {CloneCdSheet.Time(position + start)}\n");
            else
                cue.Append($"    INDEX 01 {CloneCdSheet.Time(position)}\n");
            previous = (track, position);
        }

        return new PlaybackCue(Path.GetFileNameWithoutExtension(tocFileName) + ".cue", Encoding.UTF8.GetBytes(cue.ToString()), tracks, null);
    }

    private const int SectorBytes = 2352;

    private static TocSheet Fail(string error) => new([], [], error);

    private static List<TocSheetToken> Tokenize(string text)
    {
        var tokens = new List<TocSheetToken>();
        var number = 0;
        foreach (var line in CueSheet.Lines(text))
        {
            number++;
            TokenizeLine(line, number, tokens);
            tokens.Add(new TocSheetToken(Kind.EndOfLine, "", "", number));
        }

        return tokens;
    }

    /// <summary>Adds the line's tokens; an error token ends the line, since parsing stops when it reaches it.</summary>
    private static void TokenizeLine(string line, int number, List<TocSheetToken> tokens)
    {
        bool IsComment(int at) => line[at] == '/' && at + 1 < line.Length && line[at + 1] == '/';
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c is ' ' or '\t')
                i++;
            else if (IsComment(i))
                return;
            else if (c is '{' or '}')
            {
                tokens.Add(new TocSheetToken(Kind.Word, c.ToString(), c.ToString(), number));
                i++;
            }
            else if (c == '"')
            {
                var value = new StringBuilder();
                var j = i + 1;
                while (true)
                {
                    if (j >= line.Length)
                    {
                        tokens.Add(new TocSheetToken(Kind.Error, $"line {number}: a string isn't closed", "", number));
                        return;
                    }

                    if (line[j] == '"')
                        break;
                    if (line[j] != '\\')
                        value.Append(line[j++]);
                    else if (j + 1 < line.Length && line[j + 1] is '"' or '\\')
                    {
                        value.Append(line[j + 1]);
                        j += 2;
                    }
                    else if (j + 3 < line.Length && line[j + 1] is >= '0' and <= '3' && line[j + 2] is >= '0' and <= '7' && line[j + 3] is >= '0' and <= '7')
                    {
                        value.Append((char)Convert.ToInt32(line.Substring(j + 1, 3), 8));
                        j += 4;
                    }
                    else
                    {
                        tokens.Add(new TocSheetToken(Kind.Error, $"line {number}: a string has an unknown escape", "", number));
                        return;
                    }
                }

                tokens.Add(new TocSheetToken(Kind.String, value.ToString(), line[i..(j + 1)], number));
                i = j + 1;
            }
            else
            {
                var start = i;
                while (i < line.Length && line[i] is not (' ' or '\t' or '"' or '{' or '}') && !IsComment(i))
                    i++;
                tokens.Add(new TocSheetToken(Kind.Word, line[start..i], line[start..i], number));
            }
        }
    }

    /// <summary><c>mm:ss:ff</c> (<c>ss</c> &lt; 60, <c>ff</c> &lt; 75) or <c>0</c>, in sectors.</summary>
    internal static bool TryTime(string token, out int sectors)
    {
        sectors = 0;
        if (token == "0")
            return true;
        var parts = token.Split(':');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)))
            return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var frames)
            || seconds > 59 || frames > 74)
            return false;
        var total = ((long)minutes * 60 + seconds) * 75 + frames;
        if (total > int.MaxValue)
            return false;
        sectors = (int)total;
        return true;
    }
}
