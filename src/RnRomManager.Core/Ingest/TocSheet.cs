using System.Globalization;
using System.Text;

namespace RnRomManager.Core.Ingest;

/// <summary>What a <see cref="TocItem"/> came from: <c>FILE</c>/<c>AUDIOFILE</c> or <c>DATAFILE</c>.</summary>
public enum TocItemKind
{
    File,
    Data,
}

/// <summary>
/// One data item of a <c>.toc</c> track. <paramref name="Offset"/> is the <c>#&lt;bytes&gt;</c> offset; the start and
/// length are in sectors, a null length meaning "to the end of the file".
/// </summary>
public sealed record TocItem(string FileName, TocItemKind Kind, bool Swap, long? Offset, int? Start, int? Length);

/// <summary>One <c>TRACK</c> of a <c>.toc</c>: its mode and subchannel tokens (upper-cased), PREGAP and START in sectors, and its data items.</summary>
public sealed record TocTrack(int Number, string Mode, string? Subchannel, int? Pregap, int? Start, bool HasSilence, IReadOnlyList<TocItem> Items);

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
    private static readonly HashSet<string> TrackModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUDIO", "MODE1", "MODE1_RAW", "MODE2", "MODE2_FORM1", "MODE2_FORM2", "MODE2_FORM_MIX", "MODE2_RAW",
    };

    private static readonly HashSet<string> SubchannelModes = new(StringComparer.OrdinalIgnoreCase) { "RW", "RW_RAW" };

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
    public static PlaybackCue ForPlayback(ReadOnlySpan<byte> toc, string tocFileName, IReadOnlyCollection<string> storedTracks,
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

    private enum Kind
    {
        Word,
        String,
        EndOfLine,
        Error,
    }

    /// <summary>A word, a string's value (<paramref name="Raw"/> keeps its quotes), a line end, or a tokenizing error.</summary>
    private readonly record struct Token(Kind Kind, string Text, string Raw, int Line);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var number = 0;
        foreach (var line in CueSheet.Lines(text))
        {
            number++;
            TokenizeLine(line, number, tokens);
            tokens.Add(new Token(Kind.EndOfLine, "", "", number));
        }

        return tokens;
    }

    /// <summary>Adds the line's tokens; an error token ends the line, since parsing stops when it reaches it.</summary>
    private static void TokenizeLine(string line, int number, List<Token> tokens)
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
                tokens.Add(new Token(Kind.Word, c.ToString(), c.ToString(), number));
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
                        tokens.Add(new Token(Kind.Error, $"line {number}: a string isn't closed", "", number));
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
                        tokens.Add(new Token(Kind.Error, $"line {number}: a string has an unknown escape", "", number));
                        return;
                    }
                }

                tokens.Add(new Token(Kind.String, value.ToString(), line[i..(j + 1)], number));
                i = j + 1;
            }
            else
            {
                var start = i;
                while (i < line.Length && line[i] is not (' ' or '\t' or '"' or '{' or '}') && !IsComment(i))
                    i++;
                tokens.Add(new Token(Kind.Word, line[start..i], line[start..i], number));
            }
        }
    }

    private sealed class TrackState(int number, string mode, string? subchannel)
    {
        public readonly int Number = number;
        public readonly List<TocItem> Items = [];
        public int? Pregap;
        public int? Start;
        public bool HasSilence;

        /// <summary>The summed length of the track's items so far, or null once one has no length (for a bare START).</summary>
        public int? Known = 0;

        public TocTrack Build() => new(Number, mode, subchannel, Pregap, Start, HasSilence, Items);
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _position;
        private int _line;
        private string _word = "";

        public readonly List<TrackState> Tracks = [];

        private TrackState? Track => Tracks.Count == 0 ? null : Tracks[^1];

        private Token Peek() => _position < tokens.Count ? tokens[_position] : new Token(Kind.EndOfLine, "", "", _line);

        private Token Next() => _position < tokens.Count ? tokens[_position++] : new Token(Kind.EndOfLine, "", "", _line);

        public string? Run()
        {
            while (_position < tokens.Count)
            {
                var token = Next();
                if (token.Kind == Kind.EndOfLine)
                    continue;
                if (token.Kind == Kind.Error)
                    return token.Text;
                _line = token.Line;
                _word = token.Text.ToUpperInvariant();
                if (Statement(token) is { } error)
                    return error;
            }

            return null;
        }

        private string? Statement(Token keyword)
        {
            if (keyword.Kind == Kind.String)
                return $"line {_line}: unknown statement '{keyword.Raw}'";
            switch (_word)
            {
                case "CD_DA" or "CD_ROM" or "CD_ROM_XA" or "CD_I":
                    return BeforeTracks() ?? End();
                case "CATALOG":
                    return BeforeTracks() ?? String(out _) ?? End();
                case "CD_TEXT":
                    return SkipBlock() ?? End();
                case "TRACK":
                    return TrackStatement();
            }

            if (_word is not ("COPY" or "PRE_EMPHASIS" or "TWO_CHANNEL_AUDIO" or "FOUR_CHANNEL_AUDIO" or "NO" or "ISRC"
                or "INDEX" or "PREGAP" or "START" or "SILENCE" or "ZERO" or "FILE" or "AUDIOFILE" or "DATAFILE"))
                return $"line {_line}: unknown statement '{keyword.Raw}'";
            if (Track is not { } track)
                return $"line {_line}: {_word} must come after a TRACK";
            switch (_word)
            {
                case "COPY" or "PRE_EMPHASIS" or "TWO_CHANNEL_AUDIO" or "FOUR_CHANNEL_AUDIO":
                    return End();
                case "NO":
                {
                    if (Required(out var what) is { } error)
                        return error;
                    return what.Kind == Kind.Word && what.Text.ToUpperInvariant() is "COPY" or "PRE_EMPHASIS"
                        ? End() : Unexpected(what);
                }
                case "ISRC":
                    return String(out _) ?? End();
                case "INDEX":
                    return Time(out _) ?? End();
                case "PREGAP":
                {
                    if (Time(out var pregap) is { } error)
                        return error;
                    track.Pregap = pregap;
                    return End();
                }
                case "START":
                {
                    if (Peek() is { Kind: Kind.EndOfLine })
                    {
                        if (track.Known is not { } known)
                            return $"line {_line}: START without a time needs data of known length before it";
                        track.Start = known;
                        return End();
                    }

                    if (Time(out var start) is { } error)
                        return error;
                    track.Start = start;
                    return End();
                }
                case "SILENCE":
                    return Silence(track);
                case "ZERO":
                {
                    Optional(TrackModes.Contains, "MODE0", out var error);
                    if (error is null)
                        Optional(SubchannelModes.Contains, null, out error);
                    return error ?? Silence(track);
                }
                case "FILE" or "AUDIOFILE":
                {
                    if (String(out var name) is { } error)
                        return error;
                    var swap = Optional(w => w.Equals("SWAP", StringComparison.OrdinalIgnoreCase), null, out error);
                    if (error is not null)
                        return error;
                    if (Offset(out var offset) is { } badOffset)
                        return badOffset;
                    if (Time(out var start) is { } badStart)
                        return badStart;
                    int? length = null;
                    if (Peek().Kind != Kind.EndOfLine)
                    {
                        if (Time(out var given) is { } bad)
                            return bad;
                        length = given == 0 ? null : given;
                    }

                    return AddItem(track, new TocItem(name, TocItemKind.File, swap, offset, start, length));
                }
                default: // DATAFILE
                {
                    if (String(out var name) is { } error)
                        return error;
                    if (Offset(out var offset) is { } badOffset)
                        return badOffset;
                    int? length = null;
                    if (Peek().Kind != Kind.EndOfLine)
                    {
                        if (Time(out var given) is { } bad)
                            return bad;
                        length = given;
                    }

                    return AddItem(track, new TocItem(name, TocItemKind.Data, false, offset, null, length));
                }
            }
        }

        private string? TrackStatement()
        {
            if (Required(out var mode) is { } error)
                return error;
            if (mode.Kind != Kind.Word || !TrackModes.Contains(mode.Text))
                return $"line {_line}: unknown track mode '{mode.Raw}'";
            string? subchannel = null;
            var next = Peek();
            if (next.Kind == Kind.Error)
                return Next().Text;
            if (next.Kind != Kind.EndOfLine)
            {
                Next();
                if (next.Kind != Kind.Word || !SubchannelModes.Contains(next.Text))
                    return $"line {_line}: unknown subchannel mode '{next.Raw}'";
                subchannel = next.Text.ToUpperInvariant();
            }

            Tracks.Add(new TrackState(Tracks.Count + 1, mode.Text.ToUpperInvariant(), subchannel));
            return End();
        }

        private string? Silence(TrackState track)
        {
            if (Time(out var length) is { } error)
                return error;
            track.HasSilence = true;
            track.Known += length;
            return End();
        }

        private string? AddItem(TrackState track, TocItem item)
        {
            track.Items.Add(item);
            track.Known = item.Length is { } length ? track.Known + length : null;
            return End();
        }

        private string? BeforeTracks() => Tracks.Count == 0 ? null : $"line {_line}: {_word} must come before the first TRACK";

        /// <summary>Skips a <c>{ … }</c> block, counting braces outside strings, across lines.</summary>
        private string? SkipBlock()
        {
            if (Required(out var open) is { } error)
                return error;
            if (open is not { Kind: Kind.Word, Text: "{" })
                return Unexpected(open);
            for (var depth = 1; depth > 0;)
            {
                if (_position >= tokens.Count)
                    return "the .toc ends inside a CD_TEXT block";
                var token = Next();
                if (token.Kind == Kind.Error)
                    return token.Text;
                if (token.Kind == Kind.Word)
                    depth += token.Text switch { "{" => 1, "}" => -1, _ => 0 };
            }

            return null;
        }

        /// <summary>The next token of the statement; the line's end is "incomplete".</summary>
        private string? Required(out Token token)
        {
            token = Next();
            return token.Kind switch
            {
                Kind.EndOfLine => $"line {_line}: {_word} is incomplete",
                Kind.Error => token.Text,
                _ => null,
            };
        }

        /// <summary>Consumes the next word when <paramref name="matches"/> (or it equals <paramref name="also"/>).</summary>
        private bool Optional(Func<string, bool> matches, string? also, out string? error)
        {
            var token = Peek();
            error = token.Kind == Kind.Error ? token.Text : null;
            if (token.Kind != Kind.Word || !(matches(token.Text) || token.Text.Equals(also, StringComparison.OrdinalIgnoreCase)))
                return false;
            Next();
            return true;
        }

        private string? String(out string value)
        {
            value = "";
            if (Required(out var token) is { } error)
                return error;
            if (token.Kind != Kind.String)
                return $"line {_line}: {_word} needs a string";
            value = token.Text;
            return null;
        }

        private string? Time(out int sectors)
        {
            sectors = 0;
            if (Required(out var token) is { } error)
                return error;
            return token.Kind == Kind.Word && TryTime(token.Text, out sectors) ? null : $"line {_line}: '{token.Raw}' isn't a time";
        }

        /// <summary>An optional <c>#&lt;bytes&gt;</c>.</summary>
        private string? Offset(out long? offset)
        {
            offset = null;
            var token = Peek();
            if (token.Kind == Kind.Error)
                return token.Text;
            if (token.Kind != Kind.Word || !token.Text.StartsWith('#'))
                return null;
            Next();
            var digits = token.Text[1..];
            if (digits.Length == 0 || !digits.All(char.IsAsciiDigit) || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
                return $"line {_line}: '{token.Raw}' isn't a byte offset";
            offset = bytes;
            return null;
        }

        private string? End()
        {
            var token = Next();
            return token.Kind switch
            {
                Kind.EndOfLine => null,
                Kind.Error => token.Text,
                _ => Unexpected(token),
            };
        }

        private string Unexpected(Token token) => $"line {_line}: unexpected '{token.Raw}'";
    }

    /// <summary><c>mm:ss:ff</c> (<c>ss</c> &lt; 60, <c>ff</c> &lt; 75) or <c>0</c>, in sectors.</summary>
    private static bool TryTime(string token, out int sectors)
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
