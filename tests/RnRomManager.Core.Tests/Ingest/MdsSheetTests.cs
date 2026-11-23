using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Ingest;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>File type gaps D7, D8a: reading an Alcohol 120% <c>.mds</c> and the playback cue built from it.</summary>
public class MdsSheetTests
{
    private const int Sector = 2352;

    /// <summary>One session-1 block; a null <paramref name="Pregap"/> writes <c>extra_offset</c> 0.</summary>
    private sealed record Block(int Point, int Mode = 0xAB, int Sub = 0, int SectorSize = Sector, ulong Offset = 0, uint? Pregap = 150);

    /// <summary>Header at 0, session block at 88, track blocks at 112, then one 8-byte extra block per track block.</summary>
    private static byte[] Mds(Block[] blocks, int sessions = 1, int version = 1)
    {
        var bytes = new byte[112 + blocks.Length * 88];
        "MEDIA DESCRIPTOR"u8.CopyTo(bytes);
        bytes[16] = (byte)version;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x14), (ushort)sessions);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x50), 88);
        bytes[88 + 10] = (byte)blocks.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88 + 20), 112);
        for (var i = 0; i < blocks.Length; i++)
        {
            var (at, extra) = (112 + i * 80, 112 + blocks.Length * 80 + i * 8);
            var b = blocks[i];
            bytes[at] = (byte)b.Mode;
            bytes[at + 1] = (byte)b.Sub;
            bytes[at + 4] = (byte)b.Point;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 16), (ushort)b.SectorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 36), (uint)(b.Offset / Sector));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 40), b.Offset);
            if (b.Pregap is { } pregap)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 12), (uint)extra);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extra), pregap);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(extra + 4), 1000);
            }
        }

        return bytes;
    }

    private static PlaybackCue Play(byte[] mds, long mdfLength = 3000 * Sector, string prefix = "track-") =>
        MdsSheet.ForPlayback(mds, "Game (USA).mds", "Game (USA).mdf", mdfLength, prefix);

    [Test]
    public void ForPlayback_AOneTrackMode2Disc_GivesTheCue()
    {
        var cue = Play(Mds([new Block(1)]), 1000 * Sector);

        Assert.That(cue.Error, Is.Null);
        Assert.That(cue.CueName, Is.EqualTo("Game (USA).cue"));
        Assert.That(cue.Tracks, Is.EqualTo(new[] { new PlaybackTrack("Game (USA).mdf", "track-01.bin") }));
        Assert.That(Encoding.UTF8.GetString(cue.Content), Is.EqualTo("FILE \"track-01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"));
    }

    [Test]
    public void ForPlayback_DataAndTwoAudioTracks_SkipLeadInBlocks_AndWriteOnlyLaterPregaps()
    {
        var mds = Mds([
            new Block(0xA0), new Block(0xA1), new Block(0xA2), new Block(0),
            new Block(1, Mode: 0xAA),
            new Block(2, Mode: 0xA9, Offset: 1000 * Sector),
            new Block(3, Mode: 0xA9, Offset: 2000 * Sector, Pregap: null),
        ]);

        var sheet = MdsSheet.Parse(mds);
        var cue = Play(mds, prefix: "disc2-track-");

        Assert.That(sheet.Tracks.Select(t => (t.Number, t.Pregap, t.Length)), Is.EqualTo(new[] { (1, 150L, 1000L), (2, 150L, 1000L), (3, 0L, 0L) }));
        Assert.That(cue.Error, Is.Null);
        Assert.That(Encoding.UTF8.GetString(cue.Content), Is.EqualTo(
            "FILE \"disc2-track-01.bin\" BINARY\n"
            + "  TRACK 01 MODE1/2352\n"
            + "    INDEX 01 00:00:00\n"
            + "  TRACK 02 AUDIO\n"
            + "    PREGAP 00:02:00\n"
            + "    INDEX 01 00:13:25\n"
            + "  TRACK 03 AUDIO\n"
            + "    INDEX 01 00:26:50\n"));
    }

    private static IEnumerable<TestCaseData> ParseErrors()
    {
        var one = Mds([new Block(1)]);
        var wrong = (byte[])one.Clone();
        wrong[0] = (byte)'X';
        var badSession = (byte[])one.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badSession.AsSpan(0x50), (uint)one.Length - 23);
        var badExtra = (byte[])one.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badExtra.AsSpan(112 + 12), (uint)one.Length - 7);
        yield return new TestCaseData(new byte[IngestSetPlanner.MaxCueBytes + 1], "the .mds is larger than 65536 bytes").SetName("Oversized");
        yield return new TestCaseData(one[..87], "the .mds has no MEDIA DESCRIPTOR signature").SetName("Short");
        yield return new TestCaseData(wrong, "the .mds has no MEDIA DESCRIPTOR signature").SetName("WrongSignature");
        yield return new TestCaseData(Mds([new Block(1)], version: 2), "only version 1 .mds files are supported").SetName("Version2");
        yield return new TestCaseData(Mds([new Block(1)], sessions: 0), "the .mds has no session").SetName("NoSession");
        yield return new TestCaseData(badSession, "the .mds is truncated").SetName("TruncatedSession");
        yield return new TestCaseData(one[..(112 + 79)], "the .mds is truncated").SetName("TruncatedBlock");
        yield return new TestCaseData(badExtra, "the .mds is truncated").SetName("TruncatedExtra");
        yield return new TestCaseData(Mds([new Block(0xA0), new Block(0)]), "the .mds has no track 1").SetName("NoTrack");
        yield return new TestCaseData(Mds([new Block(2)]), "track 2 follows the start").SetName("Track2First");
        yield return new TestCaseData(Mds([new Block(1), new Block(3, Offset: Sector)]), "track 3 follows track 1").SetName("Gap");
    }

    [TestCaseSource(nameof(ParseErrors))]
    public void Parse_Errors(byte[] mds, string error)
    {
        var sheet = MdsSheet.Parse(mds);

        Assert.That(sheet.Error, Is.EqualTo(error));
        Assert.That(sheet.Tracks, Is.Empty);
        Assert.That(Play(mds).Error, Is.EqualTo(error));
    }

    private static IEnumerable<TestCaseData> PlayErrors()
    {
        static TestCaseData Case(string name, byte[] mds, string error, long mdfLength = 3000 * Sector) =>
            new TestCaseData(mds, mdfLength, error).SetName(name);
        yield return Case("TwoSessions", Mds([new Block(1)], sessions: 2), "multi-session .mds images aren't supported");
        yield return Case("Subchannel", Mds([new Block(1), new Block(2, Sub: 8, SectorSize: 2448, Offset: Sector)]),
            "the .mdf stores subchannel data, which can't be played");
        yield return Case("SectorSize", Mds([new Block(1), new Block(2, Mode: 0xAE, SectorSize: 2448, Offset: Sector)]),
            "track 2 has 2448-byte sectors; only 2352 can be played");
        yield return Case("UnknownMode", Mds([new Block(1, Offset: 1), new Block(2, Mode: 0xAE, Offset: Sector)]),
            "track 2 has an unknown mode 0xAE");
        yield return Case("PartSector", Mds([new Block(1, Offset: 1)]), "the .mdf isn't a whole number of 2352-byte sectors", 2353);
        yield return Case("Misaligned", Mds([new Block(1), new Block(2, Offset: Sector + 100), new Block(3, Offset: 0)]),
            "track 2 doesn't start on a 2352-byte sector");
        yield return Case("NotAfter", Mds([new Block(1, Offset: Sector), new Block(2, Offset: Sector)]), "track 2 doesn't start after track 1");
        yield return Case("PastEnd", Mds([new Block(1), new Block(2, Offset: 10 * Sector)]), "track 2 starts past the end of the .mdf", 10 * Sector);
    }

    [TestCaseSource(nameof(PlayErrors))]
    public void ForPlayback_Errors(byte[] mds, long mdfLength, string error)
    {
        var cue = Play(mds, mdfLength);

        Assert.That(cue.Error, Is.EqualTo(error));
        Assert.That(cue.Tracks, Is.Empty);
    }
}
