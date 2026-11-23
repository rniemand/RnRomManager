using System.Text;
using RnRomManager.Core.Ingest;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>File type gaps D3–D5: reading a cdrdao <c>.toc</c>.</summary>
public class TocSheetTests
{
    private static TocSheet Parse(string toc) => TocSheet.Parse(Encoding.UTF8.GetBytes(toc));

    /// <summary>A track's fields without its items, which are compared on their own.</summary>
    private static (int, string, string?, int?, int?, bool) Fields(TocTrack t) => (t.Number, t.Mode, t.Subchannel, t.Pregap, t.Start, t.HasSilence);

    [Test]
    public void Parse_AOneFileToc_GivesItsTracks()
    {
        var sheet = Parse("""
            CD_ROM_XA
            CATALOG "0000000000000"
            // a data track and two audio tracks in one file
            TRACK MODE2_RAW
            NO COPY
            DATAFILE "Game (USA).bin" #0 10:00:00 // data
            TRACK AUDIO
            COPY
            ISRC "USABC0000001"
            FILE "Game (USA).bin" #105840000 0 03:00:00
            START 00:02:00
            INDEX 00:30:00
            TRACK AUDIO
            PREGAP 00:02:00
            FILE "game (usa).bin" #137592000 00:00:00
            """);

        Assert.That(sheet.Error, Is.Null);
        Assert.That(sheet.Files, Is.EqualTo(new[] { "Game (USA).bin" }));
        Assert.That(sheet.Tracks.Select(Fields), Is.EqualTo(new[]
        {
            (1, "MODE2_RAW", (string?)null, (int?)null, (int?)null, false),
            (2, "AUDIO", null, null, 150, false),
            (3, "AUDIO", null, 150, null, false),
        }));
        Assert.That(sheet.Tracks[0].Items, Is.EqualTo(new[] { new TocItem("Game (USA).bin", TocItemKind.Data, false, 0, null, 45_000) }));
        Assert.That(sheet.Tracks[1].Items, Is.EqualTo(new[] { new TocItem("Game (USA).bin", TocItemKind.File, false, 105_840_000, 0, 13_500) }));
        Assert.That(sheet.Tracks[2].Items, Is.EqualTo(new[] { new TocItem("game (usa).bin", TocItemKind.File, false, 137_592_000, 0, null) }));
    }

    [Test]
    public void Parse_ATwoFileToc_WithCdTextBomMixedLineEndsAndCase()
    {
        var toc = "cd_da\r"
            + "CD_TEXT {\r\n"
            + "  LANGUAGE_MAP { 0 : EN }\n"
            + "  LANGUAGE 0 { TITLE \"a } { brace\" TOC_INFO1 { 0, 10 } }\r"
            + "}\n"
            + "track audio rw_raw\n"
            + "Cd_Text { LANGUAGE 0 { TITLE \"t\" } }\r\n"
            + "zero mode0 00:00:10\n"
            + "file \"Track \\\"1\\\" \\\\ \\101.bin\" swap #0 0 00:01:00\n"
            + "start\n"
            + "TRACK AUDIO RW\n"
            + "AUDIOFILE \"two.bin\" 00:00:05 0\n";
        var sheet = TocSheet.Parse([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(toc)]);

        Assert.That(sheet.Error, Is.Null);
        Assert.That(sheet.Files, Is.EqualTo(new[] { "Track \"1\" \\ A.bin", "two.bin" }));
        Assert.That(sheet.Tracks.Select(Fields), Is.EqualTo(new[]
        {
            (1, "AUDIO", "RW_RAW", (int?)null, (int?)85, true),
            (2, "AUDIO", "RW", null, null, false),
        }));
        Assert.That(sheet.Tracks[0].Items, Is.EqualTo(new[] { new TocItem("Track \"1\" \\ A.bin", TocItemKind.File, true, 0, 0, 75) }));
        Assert.That(sheet.Tracks[1].Items, Is.EqualTo(new[] { new TocItem("two.bin", TocItemKind.File, false, null, 5, null) }));
    }

    [TestCase("TRACK AUDIO\nFILE \"a.bin 0", "line 2: a string isn't closed")]
    [TestCase("TRACK AUDIO\nFILE \"a\\q.bin\" 0", "line 2: a string has an unknown escape")]
    [TestCase("TRACK AUDIO\nFILE \"a\\400.bin\" 0", "line 2: a string has an unknown escape")]
    [TestCase("Foo \"x", "line 1: unknown statement 'Foo'")]
    [TestCase("FIRST_TRACK_NO 1", "line 1: unknown statement 'FIRST_TRACK_NO'")]
    [TestCase("file \"a.bin\" 0", "line 1: FILE must come after a TRACK")]
    [TestCase("TRACK AUDIO\ncd_rom", "line 2: CD_ROM must come before the first TRACK")]
    [TestCase("TRACK MODE0", "line 1: unknown track mode 'MODE0'")]
    [TestCase("TRACK AUDIO rw_x", "line 1: unknown subchannel mode 'rw_x'")]
    [TestCase("TRACK AUDIO\nSILENCE 588", "line 2: '588' isn't a time")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" 00:60:00", "line 2: '00:60:00' isn't a time")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" #12x 0", "line 2: '#12x' isn't a byte offset")]
    [TestCase("TRACK AUDIO\nFILE a.bin 0", "line 2: FILE needs a string")]
    [TestCase("TRACK", "line 1: TRACK is incomplete")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\"", "line 2: FILE is incomplete")]
    [TestCase("TRACK AUDIO\nNO", "line 2: NO is incomplete")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" 0\nSTART", "line 3: START without a time needs data of known length before it")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" 0 0 extra", "line 2: unexpected 'extra'")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" 0 0 \"x", "line 2: a string isn't closed")]
    [TestCase("TRACK AUDIO\nNO FOO", "line 2: unexpected 'FOO'")]
    [TestCase("CD_TEXT {\nLANGUAGE_MAP { 0 : EN }", "the .toc ends inside a CD_TEXT block")]
    [TestCase("CD_ROM", "the .toc has no TRACK")]
    [TestCase("TRACK AUDIO\nTRACK AUDIO\nFILE \"a.bin\" 0", "track 1 has no data")]
    [TestCase("TRACK AUDIO\nSILENCE 00:02:00", "the .toc names no file")]
    public void Parse_Errors(string toc, string error)
    {
        var sheet = Parse(toc);

        Assert.That(sheet.Error, Is.EqualTo(error));
        Assert.That(sheet.IsValid, Is.False);
        Assert.That(sheet.Files, Is.Empty);
        Assert.That(sheet.Tracks, Is.Empty);
    }

    [Test]
    public void Parse_Over64KiB_IsAnError() =>
        Assert.That(TocSheet.Parse(new byte[IngestSetPlanner.MaxCueBytes + 1]).Error, Is.EqualTo("the .toc is larger than 65536 bytes"));

    [Test]
    public void Parse_A100thTrack_IsAnError() =>
        Assert.That(Parse(string.Concat(Enumerable.Repeat("TRACK AUDIO\nFILE \"a.bin\" 0\n", 100))).Error,
            Is.EqualTo("the .toc has more than 99 tracks"));

    private static PlaybackCue Play(string toc, string[] stored, string prefix = "track-") =>
        TocSheet.ForPlayback(Encoding.UTF8.GetBytes(toc), "Game (USA).toc", stored, prefix);

    private static string Text(PlaybackCue cue) => Encoding.UTF8.GetString(cue.Content);

    [Test]
    public void ForPlayback_OneFileWithOffsetsStartAndPregap_GivesTheCue()
    {
        var cue = Play("""
            CD_ROM_XA
            TRACK MODE2_RAW
            FILE "Game (USA).bin" #0 0 10:00:00
            TRACK AUDIO
            FILE "Game (USA).bin" #105840000 0 03:00:00
            START 00:02:00
            TRACK AUDIO
            PREGAP 00:02:00
            FILE "game (usa).bin" #137592000 0
            """, ["Game (USA).bin"]);

        Assert.That(cue.Error, Is.Null);
        Assert.That(cue.CueName, Is.EqualTo("Game (USA).cue"));
        Assert.That(cue.Tracks, Is.EqualTo(new[] { new PlaybackTrack("Game (USA).bin", "track-01.bin") }));
        Assert.That(Text(cue), Is.EqualTo(
            "FILE \"track-01.bin\" BINARY\n"
            + "  TRACK 01 MODE2/2352\n"
            + "    INDEX 01 00:00:00\n"
            + "  TRACK 02 AUDIO\n"
            + "    INDEX 00 10:00:00\n"
            + "    INDEX 01 10:02:00\n"
            + "  TRACK 03 AUDIO\n"
            + "    PREGAP 00:02:00\n"
            + "    INDEX 01 13:00:00\n"));
    }

    [Test]
    public void ForPlayback_TwoFiles_GiveTwoFileLines()
    {
        var cue = Play("""
            TRACK MODE1_RAW
            DATAFILE "Data.bin"
            TRACK AUDIO
            FILE "Audio.bin" 0
            """, ["audio.bin", "Data.bin"], "disc2-track-");

        Assert.That(cue.Error, Is.Null);
        Assert.That(cue.Tracks, Is.EqualTo(new[]
        {
            new PlaybackTrack("Data.bin", "disc2-track-01.bin"), new PlaybackTrack("audio.bin", "disc2-track-02.bin"),
        }));
        Assert.That(Text(cue), Is.EqualTo(
            "FILE \"disc2-track-01.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
            + "FILE \"disc2-track-02.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n"));
    }

    [Test]
    public void ForPlayback_ADataFileWithoutOffset_ContinuesAfterTheSizedTrack()
    {
        var cue = Play("TRACK MODE2_RAW\nDATAFILE \"a.bin\" 00:10:00\nTRACK MODE2_RAW\nDATAFILE \"a.bin\"\n", ["a.bin"]);

        Assert.That(Text(cue), Is.EqualTo(
            "FILE \"track-01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n  TRACK 02 MODE2/2352\n    INDEX 01 00:10:00\n"));
    }

    [TestCase("CD_ROM", "a.bin", "the .toc has no TRACK")]
    [TestCase("TRACK AUDIO\nFILE \"b.bin\" 0", "a.bin", "'b.bin' isn't part of the release")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" 0", "x.bin|a.bin|c.bin", "the .toc doesn't reference stored track(s): c.bin, x.bin")]
    [TestCase("TRACK mode1\nFILE \"a.bin\" 0", "a.bin", "track 1: MODE1 can't be played; only AUDIO, MODE1_RAW and MODE2_RAW can")]
    [TestCase("TRACK AUDIO RW\nFILE \"a.bin\" 0", "a.bin", "track 1 stores subchannel data")]
    [TestCase("TRACK AUDIO\nSILENCE 00:00:10\nFILE \"a.bin\" 0", "a.bin", "track 1 uses SILENCE or ZERO")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" 0 00:00:10\nFILE \"a.bin\" 00:00:10", "a.bin", "track 1 has more than one FILE or DATAFILE")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" SWAP 0", "a.bin", "track 1 byte-swaps its audio (SWAP)")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" 0 1:0:0\nTRACK AUDIO\nFILE \"b.bin\" 0\nTRACK AUDIO\nFILE \"A.bin\" 0", "a.bin|b.bin",
        "'A.bin' is used again after another file")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" 0\nTRACK AUDIO\nFILE \"a.bin\" 00:01:00", "a.bin", "track 1 has no length but isn't the last track of 'a.bin'")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" #100 0", "a.bin", "track 1: #100 isn't a whole number of 2352-byte sectors")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" 0 00:01:00\nTRACK AUDIO\nFILE \"a.bin\" 00:00:10", "a.bin", "track 2 starts before track 1 ends in 'a.bin'")]
    [TestCase("TRACK AUDIO\nFILE \"a.bin\" SWAP 0 1:0:0\nTRACK MODE1\nFILE \"a.bin\" 1:0:0", "a.bin", "track 1 byte-swaps its audio (SWAP)")]
    public void ForPlayback_Errors(string toc, string stored, string error)
    {
        var cue = Play(toc, stored.Split('|'));

        Assert.That(cue.Error, Is.EqualTo(error));
        Assert.That(cue.Tracks, Is.Empty);
    }

    [Test]
    public void Parse_A100thFile_IsAnError() =>
        Assert.That(Parse("TRACK AUDIO\n" + string.Concat(Enumerable.Range(1, 100).Select(i => $"FILE \"{i}.bin\" 0 0\n"))).Error,
            Is.EqualTo("the .toc names more than 99 files"));
}
