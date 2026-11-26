using System.Buffers.Binary;
using System.Text;

namespace RnRomManager.Core.Ingest;

/// <summary>
/// Browser play format gaps N2–N4: the tracks of a Nero <c>.nrg</c> (big-endian), read as libmirage's <c>image-nrg</c> does
/// (<c>cdemu/cdemu</c> <c>libmirage/images/image-nrg/image-nrg.h</c> and <c>parser.c</c>). Only single-session DAO images
/// are read: a v2 footer (<c>NER5</c>) picks <c>DAOX</c>/<c>ETN2</c>, a v1 footer (<c>NERO</c>) <c>DAOI</c>/<c>ETNF</c>.
/// </summary>
/// <param name="Tracks">The tracks, numbered from the chunk's first track; empty when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the <c>.nrg</c> can't be played, or null.</param>
public sealed record NrgSheet(IReadOnlyList<NrgTrack> Tracks, string? Error)
{
    private const int SectorBytes = 2352;
    private const int DaoHeaderBytes = 22;

    public bool IsValid => Error is null;

    /// <summary>
    /// N2: the chunk start, the chunk region's end (the file length less the footer) and whether the footer is v2, read from
    /// <paramref name="tail"/> (the file's last 12 bytes, or all of it when shorter); null when there's no usable footer.
    /// </summary>
    public static (long ChunkStart, long RegionEnd, bool V2)? Footer(ReadOnlySpan<byte> tail, long length)
    {
        ulong start;
        int footer;
        if (length >= 12 && tail.Length >= 12 && tail[^12..^8].SequenceEqual("NER5"u8))
            (start, footer) = (BinaryPrimitives.ReadUInt64BigEndian(tail[^8..]), 12);
        else if (length >= 8 && tail.Length >= 8 && tail[^8..^4].SequenceEqual("NERO"u8))
            (start, footer) = (BinaryPrimitives.ReadUInt32BigEndian(tail[^4..]), 8);
        else
            return null;

        var end = length - footer;
        return start == 0 || start >= (ulong)end ? null : ((long)start, end, footer == 12);
    }

    /// <summary>N3.2–N3.12 over the chunk region <c>[chunkStart, region end)</c>.</summary>
    public static NrgSheet Parse(ReadOnlySpan<byte> region, long chunkStart, bool v2)
    {
        if (region.Length > IngestSetPlanner.MaxCueBytes)
            return Fail($"the .nrg's track list is larger than {IngestSetPlanner.MaxCueBytes} bytes");

        var daoId = v2 ? "DAOX"u8 : "DAOI"u8;
        var etnId = v2 ? "ETN2"u8 : "ETNF"u8;
        var blockBytes = v2 ? 42 : 30;
        var daos = new List<(int First, int Last, int At)>();
        var tao = false;
        for (var at = 0; at < region.Length;)
        {
            if (region.Length - at < 8)
                return Truncated();
            var id = region.Slice(at, 4);
            var size = BinaryPrimitives.ReadUInt32BigEndian(region[(at + 4)..]);
            if (id.SequenceEqual("END!"u8))
                break;
            if (size > (uint)(region.Length - at - 8))
                return Truncated();
            if (id.SequenceEqual(daoId))
            {
                if (size < DaoHeaderBytes)
                    return Truncated();
                var (first, last) = (region[at + 8 + 20], region[at + 8 + 21]);
                if (last < first || size != DaoHeaderBytes + (last - first + 1) * blockBytes)
                    return Truncated();
                daos.Add((first, last, at + 8 + DaoHeaderBytes));
            }
            else if (id.SequenceEqual(etnId))
                tao = true;

            at += 8 + (int)size;
        }

        if (daos.Count == 0)
            return Fail(tao ? "TAO .nrg images aren't supported" : "the .nrg has no track list");
        if (daos.Count > 1)
            return Fail("multi-session .nrg images aren't supported");
        var dao = daos[0];
        if (dao.First != 1)
            return Fail("the .nrg's first track isn't track 1");

        var tracks = new List<NrgTrack>();
        for (var n = dao.First; n <= dao.Last; n++)
        {
            var block = region.Slice(dao.At + (n - dao.First) * blockBytes, blockBytes);
            var offsets = new ulong[3];
            for (var i = 0; i < 3; i++)
                offsets[i] = v2 ? BinaryPrimitives.ReadUInt64BigEndian(block[(18 + i * 8)..]) : BinaryPrimitives.ReadUInt32BigEndian(block[(18 + i * 4)..]);
            tracks.Add(new NrgTrack(n, block[14], BinaryPrimitives.ReadUInt16BigEndian(block[12..]), offsets[0], offsets[1], offsets[2]));
        }

        if (tracks.FirstOrDefault(t => t.SectorSize != SectorBytes) is { } wide)
            return Fail($"track {wide.Number} has {wide.SectorSize}-byte sectors; only {SectorBytes} can be played");
        if (tracks.FirstOrDefault(t => t.Mode is not (0x05 or 0x06 or 0x07)) is { } unknown)
            return Fail($"track {unknown.Number} has an unknown mode 0x{unknown.Mode:X2}");
        if (tracks.FirstOrDefault(t => t.Index0 % SectorBytes != 0 || t.Index1 % SectorBytes != 0 || t.End % SectorBytes != 0
                || t.Index0 > t.Index1 || t.Index1 >= t.End) is { } misaligned)
            return Fail($"track {misaligned.Number} doesn't start on a {SectorBytes}-byte sector");
        for (var i = 1; i < tracks.Count; i++)
            if (tracks[i].Index0 != tracks[i - 1].End)
                return Fail($"track {tracks[i].Number} doesn't start where track {tracks[i - 1].Number} ends");
        if (tracks.FirstOrDefault(t => t.End > (ulong)chunkStart) is { } past)
            return Fail($"track {past.Number} ends past the .nrg's footer");
        return new NrgSheet(tracks, null);
    }

    /// <summary>
    /// N1, N4: a playback cue whose one FILE is the image as <c>&lt;trackPrefix&gt;01.bin</c>, and the bytes of the image
    /// to send (the last track's end, so the chunks and footer never are).
    /// </summary>
    public static (PlaybackCue Cue, long DataLength) ForPlayback(ReadOnlySpan<byte> region, long chunkStart, bool v2, string nrgFileName, string trackPrefix)
    {
        var sheet = Parse(region, chunkStart, v2);
        if (sheet.Error is { } error)
            return (PlaybackCue.Fail(error), 0);

        var alias = $"{trackPrefix}01.bin";
        var cue = new StringBuilder($"FILE \"{alias}\" BINARY\n");
        foreach (var track in sheet.Tracks)
        {
            cue.Append($"  TRACK {track.Number:00} {track.Mode switch { 0x05 => "MODE1/2352", 0x06 => "MODE2/2352", _ => "AUDIO" }}\n");
            // Cue readers start a track at its first INDEX, so track 1's bytes before index 0 are never addressed.
            if (track.Index0 < track.Index1)
                cue.Append($"    INDEX 00 {CloneCdSheet.Time((int)(track.Index0 / SectorBytes))}\n");
            cue.Append($"    INDEX 01 {CloneCdSheet.Time((int)(track.Index1 / SectorBytes))}\n");
        }

        return (new PlaybackCue(Path.GetFileNameWithoutExtension(nrgFileName) + ".cue", Encoding.UTF8.GetBytes(cue.ToString()),
            [new PlaybackTrack(nrgFileName, alias)], null), (long)sheet.Tracks[^1].End);
    }

    private static NrgSheet Truncated() => Fail("the .nrg is truncated");

    private static NrgSheet Fail(string error) => new([], error);
}
