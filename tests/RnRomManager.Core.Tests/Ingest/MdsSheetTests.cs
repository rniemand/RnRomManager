using System.Buffers.Binary;
using System.Text;
using RnRomManager.Core.Ingest;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>File type gaps D7, D8a: reading an Alcohol 120% <c>.mds</c> and the playback cue built from it.</summary>
public class MdsSheetTests
{
    private const int Sector = 2352;

    private static byte[] Mds(MdsBlock[] blocks, int sessions = 1, int version = 1) => SyntheticMdsBuilder.Build(blocks, sessions, version);

    private static PlaybackCue Play(byte[] mds, long mdfLength = 3000 * Sector, string prefix = "track-") =>
        MdsSheet.ForPlayback(mds, "Game (USA).mds", "Game (USA).mdf", mdfLength, prefix);

    [Test]
    public void ForPlayback_AOneTrackMode2Disc_GivesTheCue()
    {
        var cue = Play(Mds([new MdsBlock(1)]), 1000 * Sector);

        Assert.That(cue.Error, Is.Null);
        Assert.That(cue.CueName, Is.EqualTo("Game (USA).cue"));
        Assert.That(cue.Tracks, Is.EqualTo(new[] { new PlaybackTrack("Game (USA).mdf", "track-01.bin") }));
        Assert.That(Encoding.UTF8.GetString(cue.Content), Is.EqualTo("FILE \"track-01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"));
    }

    [Test]
    public void ForPlayback_DataAndTwoAudioTracks_SkipLeadInBlocks_AndWriteOnlyLaterPregaps()
    {
        var mds = Mds([
            new MdsBlock(0xA0), new MdsBlock(0xA1), new MdsBlock(0xA2), new MdsBlock(0),
            new MdsBlock(1, Mode: 0xAA),
            new MdsBlock(2, Mode: 0xA9, Offset: 1000 * Sector),
            new MdsBlock(3, Mode: 0xA9, Offset: 2000 * Sector, Pregap: null),
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
        var one = Mds([new MdsBlock(1)]);
        var wrong = (byte[])one.Clone();
        wrong[0] = (byte)'X';
        var badSession = (byte[])one.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badSession.AsSpan(0x50), (uint)one.Length - 23);
        var badExtra = (byte[])one.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badExtra.AsSpan(112 + 12), (uint)one.Length - 7);
        yield return new TestCaseData(new byte[IngestSetPlanner.MaxCueBytes + 1], "the .mds is larger than 65536 bytes").SetName("Oversized");
        yield return new TestCaseData(one[..87], "the .mds has no MEDIA DESCRIPTOR signature").SetName("Short");
        yield return new TestCaseData(wrong, "the .mds has no MEDIA DESCRIPTOR signature").SetName("WrongSignature");
        yield return new TestCaseData(Mds([new MdsBlock(1)], version: 2), "only version 1 .mds files are supported").SetName("Version2");
        yield return new TestCaseData(Mds([new MdsBlock(1)], sessions: 0), "the .mds has no session").SetName("NoSession");
        yield return new TestCaseData(badSession, "the .mds is truncated").SetName("TruncatedSession");
        yield return new TestCaseData(one[..(112 + 79)], "the .mds is truncated").SetName("TruncatedBlock");
        yield return new TestCaseData(badExtra, "the .mds is truncated").SetName("TruncatedExtra");
        yield return new TestCaseData(Mds([new MdsBlock(0xA0), new MdsBlock(0)]), "the .mds has no track 1").SetName("NoTrack");
        yield return new TestCaseData(Mds([new MdsBlock(2)]), "track 2 follows the start").SetName("Track2First");
        yield return new TestCaseData(Mds([new MdsBlock(1), new MdsBlock(3, Offset: Sector)]), "track 3 follows track 1").SetName("Gap");
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
        yield return Case("TwoSessions", Mds([new MdsBlock(1)], sessions: 2), "multi-session .mds images aren't supported");
        yield return Case("Subchannel", Mds([new MdsBlock(1), new MdsBlock(2, Sub: 8, SectorSize: 2448, Offset: Sector)]),
            "the .mdf stores subchannel data, which can't be played");
        yield return Case("SectorSize", Mds([new MdsBlock(1), new MdsBlock(2, Mode: 0xAE, SectorSize: 2448, Offset: Sector)]),
            "track 2 has 2448-byte sectors; only 2352 can be played");
        yield return Case("UnknownMode", Mds([new MdsBlock(1, Offset: 1), new MdsBlock(2, Mode: 0xAE, Offset: Sector)]),
            "track 2 has an unknown mode 0xAE");
        yield return Case("PartSector", Mds([new MdsBlock(1, Offset: 1)]), "the .mdf isn't a whole number of 2352-byte sectors", 2353);
        yield return Case("Misaligned", Mds([new MdsBlock(1), new MdsBlock(2, Offset: Sector + 100), new MdsBlock(3, Offset: 0)]),
            "track 2 doesn't start on a 2352-byte sector");
        yield return Case("NotAfter", Mds([new MdsBlock(1, Offset: Sector), new MdsBlock(2, Offset: Sector)]), "track 2 doesn't start after track 1");
        yield return Case("PastEnd", Mds([new MdsBlock(1), new MdsBlock(2, Offset: 10 * Sector)]), "track 2 starts past the end of the .mdf", 10 * Sector);
    }

    [TestCaseSource(nameof(PlayErrors))]
    public void ForPlayback_Errors(byte[] mds, long mdfLength, string error)
    {
        var cue = Play(mds, mdfLength);

        Assert.That(cue.Error, Is.EqualTo(error));
        Assert.That(cue.Tracks, Is.Empty);
    }
}
