using System.Globalization;
using System.Text;

namespace RnRomManager.Core.Ingest;

/// <summary>
/// CloneCD B3: the tracks of a CloneCD <c>.ccd</c>. Only <c>[Disc]</c> <c>Sessions</c>/<c>DataTracksScrambled</c> and
/// <c>[TRACK n]</c> <c>MODE</c>/<c>INDEX 0</c>/<c>INDEX 1</c> are read; every other section and key is ignored.
/// </summary>
/// <param name="Tracks">The tracks in order; empty when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the <c>.ccd</c> can't be used, or null.</param>
public sealed record CloneCdSheet(IReadOnlyList<CloneCdTrack> Tracks, string? Error)
{
    /// <summary>99:59:74, the last sector a cue time can name.</summary>
    private const int MaxSector = 449_999;

    private const int SectorBytes = 2352;

    private static readonly string[] DiscKeys = ["Sessions", "DataTracksScrambled"];
    private static readonly string[] TrackKeys = ["MODE", "INDEX 0", "INDEX 1"];

    public bool IsValid => Error is null;

    public static CloneCdSheet Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > IngestSetPlanner.MaxCueBytes)
            return Fail($"the .ccd is larger than {IngestSetPlanner.MaxCueBytes} bytes");

        var disc = new Dictionary<string, string>();
        var sections = new List<(int Number, Dictionary<string, string> Keys)>();
        Dictionary<string, string>? current = null;
        string? duplicate = null;
        foreach (var raw in CueSheet.Lines(CueSheet.Decode(bytes)))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = SectionKeys(line[1..^1].Trim(), disc, sections);
                continue;
            }

            var equals = line.IndexOf('=');
            if (current is null || equals < 0)
                continue;
            var known = current == disc ? DiscKeys : TrackKeys;
            var key = known.FirstOrDefault(k => k.Equals(line[..equals].Trim(), StringComparison.OrdinalIgnoreCase));
            if (key is not null && !current.TryAdd(key, line[(equals + 1)..].Trim()))
                duplicate ??= $"{Where(current, disc, sections)}{key} appears twice";
        }

        if (duplicate is not null)
            return Fail(duplicate);
        var numbers = new Dictionary<(Dictionary<string, string>, string), int>();
        foreach (var keys in sections.Select(s => s.Keys).Prepend(disc))
            foreach (var (key, value) in keys)
            {
                if (!value.All(char.IsAsciiDigit) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    return Fail($"{Where(keys, disc, sections)}{key} isn't a number");
                numbers[(keys, key)] = n;
            }

        if (numbers.TryGetValue((disc, "Sessions"), out var sessions) && sessions != 1)
            return Fail("multi-session CloneCD images aren't supported");
        if (numbers.TryGetValue((disc, "DataTracksScrambled"), out var scrambled) && scrambled != 0)
            return Fail("scrambled CloneCD images aren't supported");
        if (sections.Count == 0)
            return Fail("the .ccd has no [TRACK 1]");
        if (sections.Count > 99)
            return Fail("the .ccd has more than 99 tracks");
        for (var i = 0; i < sections.Count; i++)
            if (sections[i].Number != i + 1)
                return Fail(i == 0 ? $"track {sections[i].Number} follows the start" : $"track {sections[i].Number} follows track {sections[i - 1].Number}");

        var tracks = new List<CloneCdTrack>();
        foreach (var (number, keys) in sections)
        {
            int? Get(string key) => numbers.TryGetValue((keys, key), out var v) ? v : null;
            if (Get("MODE") is not { } mode)
                return Fail($"track {number} has no MODE");
            if (mode is not (0 or 1 or 2))
                return Fail($"track {number} has an unknown MODE {mode}");
            if (Get("INDEX 1") is not { } index1)
                return Fail($"track {number} has no INDEX 1");
            var index0 = Get("INDEX 0");
            if (index0 > MaxSector)
                return Fail($"track {number}: INDEX 0 is past 99:59:74");
            if (index1 > MaxSector)
                return Fail($"track {number}: INDEX 1 is past 99:59:74");
            if (index0 > index1)
                return Fail($"track {number}: INDEX 0 is after INDEX 1");
            if (tracks.Count > 0 && index1 <= tracks[^1].Index1)
                return Fail($"track {number} doesn't start after track {number - 1}");
            tracks.Add(new CloneCdTrack(number, mode, index0, index1));
        }

        return new CloneCdSheet(tracks, null);
    }

    /// <summary>
    /// CloneCD B4: a playback cue for a stored CloneCD set. Its one FILE is the <c>.img</c> as
    /// <c>&lt;trackPrefix&gt;01.img</c>; the <c>.sub</c>, when there is one, is aliased to the cue's base name so PCSX
    /// ReARMed finds it.
    /// </summary>
    public static PlaybackCue ForPlayback(
        ReadOnlySpan<byte> ccd,
        string ccdFileName,
        string imageName,
        long imageLength,
        string? subName,
        string trackPrefix)
    {
        var sheet = Parse(ccd);
        if (sheet.Error is { } error)
            return PlaybackCue.Fail(error);
        if (imageLength % SectorBytes != 0)
            return PlaybackCue.Fail($"the .img isn't a whole number of {SectorBytes}-byte sectors");
        if (sheet.Tracks[^1].Index1 >= imageLength / SectorBytes)
            return PlaybackCue.Fail($"track {sheet.Tracks[^1].Number} starts past the end of the .img");

        var stem = Path.GetFileNameWithoutExtension(ccdFileName);
        var alias = $"{trackPrefix}01.img";
        var tracks = new List<PlaybackTrack> { new(imageName, alias) };
        if (subName is not null)
            tracks.Add(new PlaybackTrack(subName, stem + ".sub"));

        var cue = new StringBuilder($"FILE \"{alias}\" BINARY\n");
        foreach (var track in sheet.Tracks)
        {
            cue.Append($"  TRACK {track.Number:00} {track.Mode switch { 0 => "AUDIO", 1 => "MODE1/2352", _ => "MODE2/2352" }}\n");
            if (track.Index0 is { } index0)
                cue.Append($"    INDEX 00 {Time(index0)}\n");
            cue.Append($"    INDEX 01 {Time(track.Index1)}\n");
        }

        return new PlaybackCue(stem + ".cue", Encoding.UTF8.GetBytes(cue.ToString()), tracks, null);
    }

    internal static string Time(int lba) => $"{lba / 75 / 60:00}:{lba / 75 % 60:00}:{lba % 75:00}";

    /// <summary>The key store of a section header: <c>[Disc]</c>, a new <c>[TRACK n]</c>, or null for one that isn't read.</summary>
    private static Dictionary<string, string>? SectionKeys(string name, Dictionary<string, string> disc,
        List<(int, Dictionary<string, string>)> sections)
    {
        if (name.Equals("Disc", StringComparison.OrdinalIgnoreCase))
            return disc;
        if (!name.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase))
            return null;
        var number = name[5..].Trim();
        if (number.Length == 0 || !number.All(char.IsAsciiDigit) || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return null;
        var keys = new Dictionary<string, string>();
        sections.Add((n, keys));
        return keys;
    }

    private static string Where(Dictionary<string, string> keys, Dictionary<string, string> disc, List<(int Number, Dictionary<string, string> Keys)> sections) =>
        keys == disc ? "[Disc] " : $"track {sections.First(s => s.Keys == keys).Number}: ";

    private static CloneCdSheet Fail(string error) => new([], error);
}
