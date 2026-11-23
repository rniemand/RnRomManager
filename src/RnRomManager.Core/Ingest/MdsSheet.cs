using System.Buffers.Binary;
using System.Text;

namespace RnRomManager.Core.Ingest;

/// <summary>
/// One track block of an <c>.mds</c> (offsets per libmirage's <c>image-mds.h</c>). <paramref name="Pregap"/> and
/// <paramref name="Length"/> come from the extra block, 0 when there is none.
/// </summary>
public sealed record MdsTrack(int Number, int Mode, int Subchannel, int SectorSize, long StartSector, ulong StartOffset, long Pregap, long Length);

/// <summary>
/// File type gaps D7: the tracks of an Alcohol 120% <c>.mds</c> (version 1, little-endian). Only session 1's blocks are
/// read; a block is a track when its <c>point</c> is 1–99.
/// </summary>
/// <param name="Sessions">The header's session count.</param>
/// <param name="Tracks">The tracks in block order; empty when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the <c>.mds</c> can't be used, or null.</param>
public sealed record MdsSheet(int Sessions, IReadOnlyList<MdsTrack> Tracks, string? Error)
{
    private const int SectorBytes = 2352;
    private const int BlockBytes = 80;

    public bool IsValid => Error is null;

    public static MdsSheet Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > IngestSetPlanner.MaxCueBytes)
            return Fail($"the .mds is larger than {IngestSetPlanner.MaxCueBytes} bytes");
        if (bytes.Length < 88 || !bytes[..16].SequenceEqual("MEDIA DESCRIPTOR"u8))
            return Fail("the .mds has no MEDIA DESCRIPTOR signature");
        if (bytes[16] != 1)
            return Fail("only version 1 .mds files are supported");
        var sessions = BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x14..]);
        if (sessions == 0)
            return Fail("the .mds has no session");

        var session = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x50..]);
        if (!Fits(bytes, session, 24))
            return Truncated();
        var blocks = bytes[(int)session + 10];
        var first = BinaryPrimitives.ReadUInt32LittleEndian(bytes[((int)session + 20)..]);
        var tracks = new List<MdsTrack>();
        for (var i = 0; i < blocks; i++)
        {
            var at = first + (long)i * BlockBytes;
            if (!Fits(bytes, at, BlockBytes))
                return Truncated();
            var block = bytes.Slice((int)at, BlockBytes);
            if (block[4] is < 1 or > 99)
                continue;
            var extra = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
            long pregap = 0, length = 0;
            if (extra != 0)
            {
                if (!Fits(bytes, extra, 8))
                    return Truncated();
                pregap = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)extra..]);
                length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[((int)extra + 4)..]);
            }

            tracks.Add(new MdsTrack(block[4], block[0], block[1], BinaryPrimitives.ReadUInt16LittleEndian(block[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(block[36..]), BinaryPrimitives.ReadUInt64LittleEndian(block[40..]), pregap, length));
        }

        if (tracks.Count == 0)
            return Fail("the .mds has no track 1");
        for (var i = 0; i < tracks.Count; i++)
            if (tracks[i].Number != i + 1)
                return Fail(i == 0 ? $"track {tracks[i].Number} follows the start" : $"track {tracks[i].Number} follows track {tracks[i - 1].Number}");
        return new MdsSheet(sessions, tracks, null);
    }

    /// <summary>
    /// File type gaps D8a: a playback cue for a stored MDS set. Its one FILE is the <c>.mdf</c> as
    /// <c>&lt;trackPrefix&gt;01.bin</c>; each check runs over every track before the next check starts.
    /// </summary>
    public static PlaybackCue ForPlayback(ReadOnlySpan<byte> mds, string mdsFileName, string mdfName, long mdfLength, string trackPrefix)
    {
        var sheet = Parse(mds);
        if (sheet.Error is { } error)
            return PlaybackCue.Fail(error);
        var tracks = sheet.Tracks;
        if (sheet.Sessions > 1)
            return PlaybackCue.Fail("multi-session .mds images aren't supported");
        if (tracks.Any(t => t.Subchannel != 0))
            return PlaybackCue.Fail("the .mdf stores subchannel data, which can't be played");
        if (tracks.FirstOrDefault(t => t.SectorSize != SectorBytes) is { } wide)
            return PlaybackCue.Fail($"track {wide.Number} has {wide.SectorSize}-byte sectors; only {SectorBytes} can be played");
        if (tracks.FirstOrDefault(t => (t.Mode & 0x07) == 6) is { } unknown)
            return PlaybackCue.Fail($"track {unknown.Number} has an unknown mode 0x{unknown.Mode:X2}");
        if (mdfLength % SectorBytes != 0)
            return PlaybackCue.Fail($"the .mdf isn't a whole number of {SectorBytes}-byte sectors");
        if (tracks.FirstOrDefault(t => t.StartOffset % SectorBytes != 0) is { } misaligned)
            return PlaybackCue.Fail($"track {misaligned.Number} doesn't start on a {SectorBytes}-byte sector");
        for (var i = 1; i < tracks.Count; i++)
            if (tracks[i].StartOffset <= tracks[i - 1].StartOffset)
                return PlaybackCue.Fail($"track {tracks[i].Number} doesn't start after track {tracks[i - 1].Number}");
        if (tracks[^1].StartOffset >= (ulong)mdfLength)
            return PlaybackCue.Fail($"track {tracks[^1].Number} starts past the end of the .mdf");

        var alias = $"{trackPrefix}01.bin";
        var cue = new StringBuilder($"FILE \"{alias}\" BINARY\n");
        foreach (var track in tracks)
        {
            cue.Append($"  TRACK {track.Number:00} {(track.Mode & 0x07) switch { 1 => "AUDIO", 2 => "MODE1/2352", _ => "MODE2/2352" }}\n");
            // Track 1's pregap is the cue's implicit two seconds; Alcohol leaves later pregaps out of the .mdf.
            if (track.Number > 1 && track.Pregap > 0)
                cue.Append($"    PREGAP {CloneCdSheet.Time((int)track.Pregap)}\n");
            cue.Append($"    INDEX 01 {CloneCdSheet.Time((int)(track.StartOffset / SectorBytes))}\n");
        }

        return new PlaybackCue(Path.GetFileNameWithoutExtension(mdsFileName) + ".cue", Encoding.UTF8.GetBytes(cue.ToString()),
            [new PlaybackTrack(mdfName, alias)], null);
    }

    private static bool Fits(ReadOnlySpan<byte> bytes, long offset, int length) => offset + length <= bytes.Length;

    private static MdsSheet Truncated() => Fail("the .mds is truncated");

    private static MdsSheet Fail(string error) => new(0, [], error);
}
