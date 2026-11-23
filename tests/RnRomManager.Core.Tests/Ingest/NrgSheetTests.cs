using System.Text;
using RnRomManager.Core.Ingest;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Browser play format gaps N2–N4: reading a Nero <c>.nrg</c>'s footer and DAO chunk, and the playback cue built from it.</summary>
public class NrgSheetTests
{
    private const ulong S = 2352;
    private const long ChunkStart = 3000 * (long)S;

    private static byte[] Region(params byte[][] chunks) => chunks.SelectMany(c => c).ToArray();

    private static byte[] Dao(bool v2, params NrgBlock[] blocks) => SyntheticNrgBuilder.Dao(blocks, v2);

    private static byte[] End => SyntheticNrgBuilder.Chunk("END!", []);

    // Data with a 150-sector index 0, then two audio tracks (the last without a pregap).
    private static readonly NrgBlock[] ThreeTracks =
    [
        new(150 * S, 300 * S, 1000 * S, Mode: 0x05),
        new(1000 * S, 1150 * S, 2000 * S),
        new(2000 * S, 2000 * S, 3000 * S),
    ];

    private const string ThreeTrackCue =
        "FILE \"track-01.bin\" BINARY\n"
        + "  TRACK 01 MODE1/2352\n"
        + "    INDEX 00 00:02:00\n"
        + "    INDEX 01 00:04:00\n"
        + "  TRACK 02 AUDIO\n"
        + "    INDEX 00 00:13:25\n"
        + "    INDEX 01 00:15:25\n"
        + "  TRACK 03 AUDIO\n"
        + "    INDEX 01 00:26:50\n";

    [TestCase(true, 12)]
    [TestCase(false, 8)]
    public void Footer_ReadsTheChunkStartRegionEndAndVersion(bool v2, int footer)
    {
        var image = SyntheticNrgBuilder.Build([new NrgBlock(0, 0, 10 * S, Mode: 0x06)], v2);

        var read = NrgSheet.Footer(image.AsSpan(^12..), image.Length);

        Assert.That(read, Is.EqualTo((10 * (long)S, image.Length - (long)footer, v2)));
    }

    private static IEnumerable<TestCaseData> NoFooters()
    {
        yield return new TestCaseData(new byte[64]).SetName("Footer_NoSignature_IsNull");
        yield return new TestCaseData("NERO\0\0\0"u8.ToArray()).SetName("Footer_AFileShorterThan8Bytes_IsNull");
        yield return new TestCaseData(Tail("NERO"u8, [0, 0, 0, 0])).SetName("Footer_AChunkStartOf0_IsNull");
        yield return new TestCaseData(Tail("NERO"u8, [0, 0, 0, 56])).SetName("Footer_AChunkStartAtTheRegionEnd_IsNull");
        yield return new TestCaseData(Tail("NER5"u8, [0xFF, 0, 0, 0, 0, 0, 0, 1])).SetName("Footer_AHugeV2ChunkStart_IsNull");
    }

    // 64 bytes ending in the footer.
    private static byte[] Tail(ReadOnlySpan<byte> signature, byte[] start)
    {
        var bytes = new byte[64];
        signature.CopyTo(bytes.AsSpan(64 - 4 - start.Length));
        start.CopyTo(bytes, 64 - start.Length);
        return bytes;
    }

    [TestCaseSource(nameof(NoFooters))]
    public void Footer_Unusable_IsNull(byte[] file) =>
        Assert.That(NrgSheet.Footer(file.AsSpan(Math.Max(0, file.Length - 12)), file.Length), Is.Null);

    private static IEnumerable<TestCaseData> ParseErrors()
    {
        TestCaseData Case(string name, byte[] region, string error, bool v2 = true) =>
            new TestCaseData(region, v2, error).SetName($"Parse_{name}_Fails");

        var one = new NrgBlock(0, 0, 10 * S);
        yield return Case("ARegionOver64KiB", new byte[IngestSetPlanner.MaxCueBytes + 1], "the .nrg's track list is larger than 65536 bytes");
        var past = SyntheticNrgBuilder.Chunk("CUEX", new byte[16]);
        yield return Case("AChunkPastTheRegion", past[..^1], "the .nrg is truncated");
        yield return Case("AFewBytesAfterTheLastChunk", Region(Dao(true, one), [1, 2, 3]), "the .nrg is truncated");
        var dao = Dao(true, one);
        yield return Case("ADaoSizeThatDoesntMatchItsBlocks", SyntheticNrgBuilder.Chunk("DAOX", [.. dao[8..], 0]), "the .nrg is truncated");
        yield return Case("ADaoLastTrackBeforeItsFirst", Dao(true), "the .nrg is truncated");
        yield return Case("AV2EtnOnly", Region(SyntheticNrgBuilder.Chunk("ETN2", new byte[32]), End), "TAO .nrg images aren't supported");
        yield return Case("AV1EtnOnly", Region(SyntheticNrgBuilder.Chunk("ETNF", new byte[20]), End), "TAO .nrg images aren't supported", v2: false);
        yield return Case("NoTrackList", Region(SyntheticNrgBuilder.Chunk("CUEX", new byte[16]), End), "the .nrg has no track list");
        yield return Case("AV2FileWithOnlyV1Chunks", Region(Dao(false, one), SyntheticNrgBuilder.Chunk("ETNF", new byte[20]), End), "the .nrg has no track list");
        yield return Case("TwoDaoChunks", Region(Dao(true, one), Dao(true, one), End), "multi-session .nrg images aren't supported");
        yield return Case("AFirstTrackOf2", Region(SyntheticNrgBuilder.Dao([one], true, firstTrack: 2), End), "the .nrg's first track isn't track 1");
        yield return Case("A2048ByteTrack", Region(Dao(true, one, new NrgBlock(10 * S, 10 * S, 20 * S, SectorSize: 2048)), End),
            "track 2 has 2048-byte sectors; only 2352 can be played");
        yield return Case("AMode0x0FTrack", Region(Dao(true, new NrgBlock(0, 0, 10 * S, Mode: 0x0F, SectorSize: 2448)), End),
            "track 1 has 2448-byte sectors; only 2352 can be played");
        yield return Case("ASectorSizeAfterAnEarlierBadMode", Region(Dao(true, new NrgBlock(0, 0, 10 * S, Mode: 0x03), new NrgBlock(10 * S, 10 * S, 20 * S, SectorSize: 2336)), End),
            "track 2 has 2336-byte sectors; only 2352 can be played");
        yield return Case("AnUnknownMode", Region(Dao(false, new NrgBlock(0, 0, 10 * S, Mode: 0x03)), End), "track 1 has an unknown mode 0x03", v2: false);
        yield return Case("AMisalignedIndex1", Region(Dao(true, new NrgBlock(0, 100, 10 * S)), End), "track 1 doesn't start on a 2352-byte sector");
        yield return Case("AnIndex0AfterIndex1", Region(Dao(true, new NrgBlock(2 * S, S, 10 * S)), End), "track 1 doesn't start on a 2352-byte sector");
        yield return Case("AnEmptyTrack", Region(Dao(true, one, new NrgBlock(10 * S, 20 * S, 20 * S)), End), "track 2 doesn't start on a 2352-byte sector");
        yield return Case("AGapBetweenTracks", Region(Dao(true, one, new NrgBlock(11 * S, 11 * S, 20 * S)), End), "track 2 doesn't start where track 1 ends");
        yield return Case("AnOverlap", Region(Dao(true, one, new NrgBlock(9 * S, 10 * S, 20 * S)), End), "track 2 doesn't start where track 1 ends");
        yield return Case("ATrackPastTheFooter", Region(Dao(true, one, new NrgBlock(10 * S, 10 * S, 3001 * S)), End), "track 2 ends past the .nrg's footer");
    }

    [TestCaseSource(nameof(ParseErrors))]
    public void Parse_Errors(byte[] region, bool v2, string error)
    {
        var sheet = NrgSheet.Parse(region, ChunkStart, v2);

        Assert.That(sheet.Error, Is.EqualTo(error));
        Assert.That(sheet.Tracks, Is.Empty);
        Assert.That(NrgSheet.ForPlayback(region, ChunkStart, v2, "Game.nrg", "track-").Cue.Error, Is.EqualTo(error));
    }

    [Test]
    public void Parse_SkipsUnknownAndOtherVersionChunks_AndReadsToTheRegionEndWithoutEnd()
    {
        var one = new NrgBlock(0, 0, 10 * S, Mode: 0x06);
        var region = Region(SyntheticNrgBuilder.Chunk("CUEX", new byte[16]), Dao(false, one), Dao(true, one), SyntheticNrgBuilder.Chunk("MTYP", new byte[4]));

        var sheet = NrgSheet.Parse(region, ChunkStart, v2: true);

        Assert.That(sheet.Error, Is.Null);
        Assert.That(sheet.Tracks, Is.EqualTo(new[] { new NrgTrack(1, 0x06, 2352, 0, 0, 10 * S) }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ForPlayback_ThreeTracks_GivesTheCueAndTheLastTracksEnd(bool v2)
    {
        var (cue, length) = NrgSheet.ForPlayback(Region(Dao(v2, ThreeTracks), End), ChunkStart, v2, "Game (USA).nrg", "track-");

        Assert.That(cue.Error, Is.Null);
        Assert.That(cue.CueName, Is.EqualTo("Game (USA).cue"));
        Assert.That(cue.Tracks, Is.EqualTo(new[] { new PlaybackTrack("Game (USA).nrg", "track-01.bin") }));
        Assert.That(Encoding.UTF8.GetString(cue.Content), Is.EqualTo(ThreeTrackCue));
        Assert.That(length, Is.EqualTo(ChunkStart));
    }

    [Test]
    public void ForPlayback_InASet_UsesTheTrackPrefix()
    {
        var (cue, _) = NrgSheet.ForPlayback(Region(Dao(true, new NrgBlock(0, 0, 10 * S, Mode: 0x06)), End), ChunkStart, true, "Game (Disc 2).nrg", "disc2-track-");

        Assert.That(Encoding.UTF8.GetString(cue.Content), Is.EqualTo("FILE \"disc2-track-01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"));
    }
}
