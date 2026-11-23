using System.Text;
using RnRomManager.Core.Ingest;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>CloneCD B3, B4: reading a <c>.ccd</c> and the playback cue built from it.</summary>
public class CloneCdSheetTests
{
    private const string OneTrack = """
        [CloneCD]
        Version=3
        [Disc]
        TocEntries=4
        Sessions=1
        DataTracksScrambled=0
        CDTextLength=0
        [Session 1]
        PreGapMode=2
        [Entry 0]
        Session=1
        Point=0xa0
        [TRACK 1]
        MODE=2
        INDEX 1=0
        """;

    private const string ThreeTracks = """
        [Disc]
        Sessions=1
        [TRACK 1]
        MODE=1
        INDEX 1=0
        [TRACK 2]
        MODE=0
        FLAGS= DCP
        INDEX 0=15000
        INDEX 1=15150
        [TRACK 3]
        MODE=0
        INDEX 0=20000
        INDEX 1=20150
        """;

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string? Error(string text) => CloneCdSheet.Parse(Bytes(text)).Error;

    private static PlaybackCue Play(string text, long imageLength = 2352L * 30000, string? sub = "Game.sub", string prefix = "track-") =>
        CloneCdSheet.ForPlayback(Bytes(text), "Game.ccd", "Game.img", imageLength, sub, prefix);

    [Test]
    public void OneTrackMode2_GivesTheExactCue()
    {
        var cue = Play(OneTrack);

        Assert.That(cue.Error, Is.Null);
        Assert.That(cue.CueName, Is.EqualTo("Game.cue"));
        Assert.That(Encoding.UTF8.GetString(cue.Content), Is.EqualTo("FILE \"track-01.img\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"));
    }

    [Test]
    public void DataAndTwoAudioTracks_GiveTheExactCue()
    {
        var cue = Play(ThreeTracks);

        Assert.That(cue.Error, Is.Null);
        Assert.That(Encoding.UTF8.GetString(cue.Content), Is.EqualTo("""
            FILE "track-01.img" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 00 03:20:00
                INDEX 01 03:22:00
              TRACK 03 AUDIO
                INDEX 00 04:26:50
                INDEX 01 04:28:50

            """.ReplaceLineEndings("\n")));
        Assert.That(cue.Content.Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [TestCase("\n")]
    [TestCase("\r\n")]
    [TestCase("\r")]
    public void LineEndings_AreAccepted(string newline)
    {
        var sheet = CloneCdSheet.Parse(Bytes(ThreeTracks.ReplaceLineEndings(newline)));

        Assert.That(sheet.Error, Is.Null);
        Assert.That(sheet.Tracks, Is.EqualTo(new[]
        {
            new CloneCdTrack(1, 1, null, 0), new CloneCdTrack(2, 0, 15000, 15150), new CloneCdTrack(3, 0, 20000, 20150),
        }));
    }

    [Test]
    public void LeadingBom_IsDropped()
    {
        Assert.That(CloneCdSheet.Parse([0xEF, 0xBB, 0xBF, .. Bytes(OneTrack)]).Error, Is.Null);
    }

    [Test]
    public void MixedCaseAndSpaces_AreIgnored()
    {
        var sheet = CloneCdSheet.Parse(Bytes("  [ disc ]\n sessions = 1 \n[ track 1 ]\nmode= 1\n index 1 =0\n"));

        Assert.That(sheet.Error, Is.Null);
        Assert.That(sheet.Tracks, Is.EqualTo(new[] { new CloneCdTrack(1, 1, null, 0) }));
    }

    [Test]
    public void IgnoredSectionsAndKeys_AreNotRead()
    {
        // Sessions=2 in [Session 1] and a duplicate TocEntries are outside what's read.
        var sheet = CloneCdSheet.Parse(Bytes("[Disc]\nTocEntries=4\nTocEntries=x\n[Session 1]\nSessions=2\n[CDText]\nMODE=9\n[TRACK 1]\nMODE=2\nFLAGS=x\nFLAGS=y\nINDEX 1=0\n"));

        Assert.That(sheet.Error, Is.Null);
    }

    [TestCase("[Disc]\nSessions=1\nsessions=1\n[TRACK 1]\nMODE=1\nINDEX 1=0", "[Disc] Sessions appears twice")]
    [TestCase("[TRACK 1]\nMODE=1\nmode=1\nINDEX 1=0", "track 1: MODE appears twice")]
    [TestCase("[Disc]\nDataTracksScrambled=no\n[TRACK 1]\nMODE=1\nINDEX 1=0", "[Disc] DataTracksScrambled isn't a number")]
    [TestCase("[TRACK 1]\nMODE=1\nINDEX 1=-1", "track 1: INDEX 1 isn't a number")]
    [TestCase("[Disc]\nSessions=2\n[TRACK 1]\nMODE=1\nINDEX 1=0", "multi-session CloneCD images aren't supported")]
    [TestCase("[Disc]\nDataTracksScrambled=1\n[TRACK 1]\nMODE=1\nINDEX 1=0", "scrambled CloneCD images aren't supported")]
    [TestCase("[Disc]\nSessions=1", "the .ccd has no [TRACK 1]")]
    [TestCase("[TRACK 1]\nMODE=1\nINDEX 1=0\n[TRACK 3]\nMODE=0\nINDEX 1=10", "track 3 follows track 1")]
    [TestCase("[TRACK 2]\nMODE=1\nINDEX 1=0", "track 2 follows the start")]
    [TestCase("[TRACK 1]\nINDEX 1=0", "track 1 has no MODE")]
    [TestCase("[TRACK 1]\nMODE=4\nINDEX 1=0", "track 1 has an unknown MODE 4")]
    [TestCase("[TRACK 1]\nMODE=1\nINDEX 0=0", "track 1 has no INDEX 1")]
    [TestCase("[TRACK 1]\nMODE=1\nINDEX 0=450000\nINDEX 1=450001", "track 1: INDEX 0 is past 99:59:74")]
    [TestCase("[TRACK 1]\nMODE=1\nINDEX 1=450000", "track 1: INDEX 1 is past 99:59:74")]
    [TestCase("[TRACK 1]\nMODE=1\nINDEX 0=10\nINDEX 1=5", "track 1: INDEX 0 is after INDEX 1")]
    [TestCase("[TRACK 1]\nMODE=1\nINDEX 1=100\n[TRACK 2]\nMODE=0\nINDEX 1=100", "track 2 doesn't start after track 1")]
    public void Errors(string text, string error)
    {
        Assert.That(Error(text), Is.EqualTo(error));
    }

    [Test]
    public void LastSector_IsAccepted()
    {
        Assert.That(Error("[TRACK 1]\nMODE=1\nINDEX 1=449999"), Is.Null);
    }

    [Test]
    public void MoreThan99Tracks_IsAnError()
    {
        var text = string.Concat(Enumerable.Range(1, 100).Select(n => $"[TRACK {n}]\nMODE=0\nINDEX 1={n}\n"));

        Assert.That(Error(text), Is.EqualTo("the .ccd has more than 99 tracks"));
    }

    [Test]
    public void TooLarge_IsAnError()
    {
        var bytes = new byte[IngestSetPlanner.MaxCueBytes + 1];

        Assert.That(CloneCdSheet.Parse(bytes).Error, Is.EqualTo("the .ccd is larger than 65536 bytes"));
        Assert.That(CloneCdSheet.ForPlayback(bytes, "Game.ccd", "Game.img", 2352, null, "track-").Error, Is.EqualTo("the .ccd is larger than 65536 bytes"));
    }

    [Test]
    public void ImageNotWholeSectors_IsAnError()
    {
        Assert.That(Play(OneTrack, imageLength: 2352 * 10 + 1).Error, Is.EqualTo("the .img isn't a whole number of 2352-byte sectors"));
    }

    [Test]
    public void LastTrackPastTheImage_IsAnError()
    {
        Assert.That(Play(ThreeTracks, imageLength: 2352L * 20150).Error, Is.EqualTo("track 3 starts past the end of the .img"));
        Assert.That(Play(ThreeTracks, imageLength: 2352L * 20151).Error, Is.Null);
    }

    [Test]
    public void Sub_IsAliasedToTheCueBaseName()
    {
        Assert.That(Play(OneTrack, sub: "Game.SUB").Tracks, Is.EqualTo(new[]
        {
            new PlaybackTrack("Game.img", "track-01.img"), new PlaybackTrack("Game.SUB", "Game.sub"),
        }));
        Assert.That(Play(OneTrack, sub: null).Tracks, Is.EqualTo(new[] { new PlaybackTrack("Game.img", "track-01.img") }));
    }

    [Test]
    public void TrackPrefix_NamesTheImage()
    {
        var cue = Play(OneTrack, prefix: "disc2-track-");

        Assert.That(cue.Tracks[0].Alias, Is.EqualTo("disc2-track-01.img"));
        Assert.That(Encoding.UTF8.GetString(cue.Content), Does.StartWith("FILE \"disc2-track-01.img\" BINARY\n"));
    }
}
