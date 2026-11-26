using System.Globalization;

namespace RnRomManager.Core.Ingest;

internal sealed class Parser(List<TocSheetToken> tokens)
{
    private int _position;
    private int _line;
    private string _word = "";

    public readonly List<TrackState> Tracks = [];

    private TrackState? Track => Tracks.Count == 0 ? null : Tracks[^1];

    private TocSheetToken Peek() => _position < tokens.Count ? tokens[_position] : new TocSheetToken(Kind.EndOfLine, "", "", _line);

    private TocSheetToken Next() => _position < tokens.Count ? tokens[_position++] : new TocSheetToken(Kind.EndOfLine, "", "", _line);

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

    private string? Statement(TocSheetToken keyword)
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
                Optional(TocSheet.TrackModes.Contains, "MODE0", out var error);
                if (error is null)
                    Optional(TocSheet.SubchannelModes.Contains, null, out error);
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
        if (mode.Kind != Kind.Word || !TocSheet.TrackModes.Contains(mode.Text))
            return $"line {_line}: unknown track mode '{mode.Raw}'";
        string? subchannel = null;
        var next = Peek();
        if (next.Kind == Kind.Error)
            return Next().Text;
        if (next.Kind != Kind.EndOfLine)
        {
            Next();
            if (next.Kind != Kind.Word || !TocSheet.SubchannelModes.Contains(next.Text))
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
    private string? Required(out TocSheetToken token)
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
        return token.Kind == Kind.Word && TocSheet.TryTime(token.Text, out sectors) ? null : $"line {_line}: '{token.Raw}' isn't a time";
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

    private string Unexpected(TocSheetToken token) => $"line {_line}: unexpected '{token.Raw}'";
}
