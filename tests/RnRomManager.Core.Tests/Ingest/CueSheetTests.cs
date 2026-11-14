using System.Text;
using RnRomManager.Core.Ingest;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Disc systems B8: the FILE names of a cue sheet.</summary>
public class CueSheetTests
{
    private static CueSheet Parse(string text) => CueSheet.Parse(Encoding.UTF8.GetBytes(text));

    private static IReadOnlyList<string> Files(string text)
    {
        var cue = Parse(text);
        Assert.That(cue.Error, Is.Null);
        return cue.Files;
    }

    [Test]
    public void QuotedAndUnquotedNames_InOrder()
    {
        const string text = """
            FILE "Game (USA) (Track 1).bin" BINARY
              TRACK 01 MODE2/2352
                INDEX 01 00:00:00
            FILE Track2.bin BINARY
              TRACK 02 AUDIO
            """;

        Assert.That(Files(text), Is.EqualTo(new[] { "Game (USA) (Track 1).bin", "Track2.bin" }));
    }

    [Test]
    public void MixedCase_AndLeadingWhitespace_AndCrLf()
    {
        Assert.That(Files("  file \"a.bin\" binary\r\n\tFiLe \"b.bin\" BINARY\rFILE\t\"c.bin\" BINARY"), Is.EqualTo(new[] { "a.bin", "b.bin", "c.bin" }));
    }

    [Test]
    public void LeadingBom_IsDropped()
    {
        var cue = CueSheet.Parse([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("FILE \"a.bin\" BINARY")]);

        Assert.That(cue.Files, Is.EqualTo(new[] { "a.bin" }));
    }

    [Test]
    public void Utf8Names_AreDecoded()
    {
        Assert.That(Files("FILE \"Pokémon (Track 1).bin\" BINARY"), Is.EqualTo(new[] { "Pokémon (Track 1).bin" }));
    }

    [Test]
    public void InvalidUtf8_FallsBackToLatin1()
    {
        var bytes = Encoding.Latin1.GetBytes("FILE \"Pokémon (Track 1).bin\" BINARY"); // é as one 0xE9 byte

        Assert.That(CueSheet.Parse(bytes).Files, Is.EqualTo(new[] { "Pokémon (Track 1).bin" }));
    }

    [Test]
    public void Duplicates_AreCollapsed_KeepingTheFirstSpelling()
    {
        Assert.That(Files("FILE \"a.bin\" BINARY\nFILE \"b.bin\" BINARY\nFILE \"a.bin\" BINARY\nFILE \"A.BIN\" BINARY"),
            Is.EqualTo(new[] { "a.bin", "b.bin" }));
    }

    [Test]
    public void OtherCommands_AreIgnored()
    {
        Assert.That(Files("REM GENRE Racing\nCATALOG 0000000000000\nTITLE \"FILE x.bin\"\nFILE \"a.bin\" BINARY\nFILES \"no.bin\""),
            Is.EqualTo(new[] { "a.bin" }));
    }

    [Test]
    public void NoFileLine_IsAnError()
    {
        var cue = Parse("REM nothing here\nTRACK 01 AUDIO");

        Assert.That((cue.IsValid, cue.Files.Count), Is.EqualTo((false, 0)));
        Assert.That(cue.Error, Does.Contain("no FILE line"));
    }

    [Test]
    public void NinetyNineDistinctNames_Parse_AndAHundredDoNot()
    {
        static string Cue(int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"FILE \"Track {i}.bin\" BINARY\n"));

        Assert.That(Files(Cue(99)), Has.Count.EqualTo(99));
        Assert.That(Parse(Cue(100)).Error, Does.Contain("more than 99"));
    }

    [TestCase("FILE \"\" BINARY")]
    [TestCase("FILE \".\" BINARY")]
    [TestCase("FILE \"..\" BINARY")]
    [TestCase("FILE \"dir/a.bin\" BINARY")]
    [TestCase("FILE \"dir\\a.bin\" BINARY")]
    [TestCase("FILE \"C:a.bin\" BINARY")]
    [TestCase("FILE \"a?.bin\" BINARY")]
    [TestCase("FILE \"CON.bin\" BINARY")]
    [TestCase("FILE \"a.bin BINARY")]
    public void BadNames_AreAnError(string line) =>
        Assert.That(Parse(line).IsValid, Is.False);

    [Test]
    public void DotsInsideAName_AreValid()
    {
        Assert.That(Files("FILE \"...Iru! (Japan).bin\" BINARY"), Is.EqualTo(new[] { "...Iru! (Japan).bin" }));
    }

    // Play B2: the playback-only copy for the pinned PCSX ReARMed parser.

    private const string TwoTrackCue = """
        REM GENRE Racing
        CATALOG 0000000000000
        FILE "Game (USA) (Track 1).bin" BINARY
          TRACK 01 MODE2/2352
            FLAGS DCP
            INDEX 01 00:00:00
        FILE "Game (USA) (Track 2).bin" BINARY
          TRACK 02 AUDIO
            TITLE "Song"
            INDEX 00 00:00:00
            INDEX 01 00:02:00
        """;

    private static readonly string[] TwoTracks = ["Game (USA) (Track 1).bin", "Game (USA) (Track 2).bin"];

    private static PlaybackCue Playback(string text, IReadOnlyCollection<string> stored, string cueName = "Game (USA).cue") =>
        CueSheet.ForPlayback(Encoding.UTF8.GetBytes(text), cueName, stored);

    private static PlaybackCue Playable(string text, IReadOnlyCollection<string> stored, string cueName = "Game (USA).cue")
    {
        var cue = Playback(text, stored, cueName);
        Assert.That(cue.Error, Is.Null);
        return cue;
    }

    private static string Text(PlaybackCue cue) => Encoding.UTF8.GetString(cue.Content);

    private static string Error(string text, IReadOnlyCollection<string> stored)
    {
        var cue = Playback(text, stored);
        Assert.That((cue.IsValid, cue.Content.Length, cue.Tracks.Count), Is.EqualTo((false, 0, 0)));
        return cue.Error!;
    }

    [Test]
    public void Playback_IsCanonical_WithAliasedTracks_AndOnlyTheParsedCommands()
    {
        var cue = Playable(TwoTrackCue, TwoTracks);

        Assert.That(Text(cue), Is.EqualTo(
            "FILE \"track-01.bin\" BINARY\n" +
            "  TRACK 01 MODE2/2352\n" +
            "    INDEX 01 00:00:00\n" +
            "FILE \"track-02.bin\" BINARY\n" +
            "  TRACK 02 AUDIO\n" +
            "    INDEX 00 00:00:00\n" +
            "    INDEX 01 00:02:00\n"));
        Assert.That(cue.Tracks, Is.EqualTo(new[]
        {
            new PlaybackTrack("Game (USA) (Track 1).bin", "track-01.bin"),
            new PlaybackTrack("Game (USA) (Track 2).bin", "track-02.bin")
        }));
        Assert.That(cue.CueName, Is.EqualTo("Game (USA).cue"));
    }

    [Test]
    public void Playback_HasNoBom_NoCrOrTabs_AndShortLines()
    {
        var cue = Playable("﻿\tfile\t\"a.bin\"\tbinary\r\n\ttrack\t1\tmode1/2352\r\n\t\tindex\t1\t0:0:0\r\n", ["a.bin"]);

        Assert.That(cue.Content.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]), Is.False);
        Assert.That(Text(cue), Is.EqualTo("FILE \"track-01.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"));
    }

    [Test]
    public void Playback_CueName_KeepsTheStoredBaseName_WithALowercaseExtension()
    {
        var cue = Playable(TwoTrackCue, TwoTracks, "Pokémon # Disc 1 (Japan).CUE");

        Assert.That(cue.CueName, Is.EqualTo("Pokémon # Disc 1 (Japan).cue"));
    }

    [TestCase("Game.bin")]
    [TestCase(".cue")]
    [TestCase("dir/Game.cue")]
    public void Playback_RejectsAnUnusableCueName(string cueName) =>
        Assert.That(Playback(TwoTrackCue, TwoTracks, cueName).IsValid, Is.False);

    [Test]
    public void Playback_UnquotedNames_AndMismatchedCase_MapToTheStoredNames()
    {
        var cue = Playable("FILE track1.BIN BINARY\n TRACK 01 MODE2/2352\n INDEX 01 00:00:00\nFILE \"TRACK2.bin\" binary\n TRACK 02 AUDIO\n INDEX 01 00:00:00",
            ["Track2.bin", "Track1.bin"]);

        Assert.That(cue.Tracks, Is.EqualTo(new[] { new PlaybackTrack("Track1.bin", "track-01.bin"), new PlaybackTrack("Track2.bin", "track-02.bin") }));
    }

    [Test]
    public void Playback_MultipleTracksInOneFile_KeepTheirModesAndPositions()
    {
        var cue = Playable("""
            FILE "Game.bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                PREGAP 00:02:00
                INDEX 01 12:34:56
              TRACK 03 audio
                INDEX 00 20:00:00
                INDEX 01 20:02:74
            """, ["Game.bin"]);

        Assert.That(Text(cue), Is.EqualTo(
            "FILE \"track-01.bin\" BINARY\n" +
            "  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
            "  TRACK 02 AUDIO\n    PREGAP 00:02:00\n    INDEX 01 12:34:56\n" +
            "  TRACK 03 AUDIO\n    INDEX 00 20:00:00\n    INDEX 01 20:02:74\n"));
        Assert.That(cue.Tracks, Has.Count.EqualTo(1));
    }

    [Test]
    public void Playback_CrOnlyInput_IsSplitIntoLines()
    {
        Assert.That(Text(Playable("FILE \"a.bin\" BINARY\r  TRACK 01 AUDIO\r    INDEX 01 00:00:00\r", ["a.bin"])),
            Is.EqualTo("FILE \"track-01.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n"));
    }

    [Test]
    public void Playback_LongNonAsciiNamesWithSpacesAndHash_StayOutOfTheShortLines()
    {
        var name = new string('é', 120) + " # (Track 1).bin"; // 256 UTF-8 bytes
        var cue = Playable($"FILE \"{name}\" BINARY\n TRACK 01 MODE2/2352\n INDEX 01 00:00:00", [name]);

        Assert.That(cue.Tracks.Single(), Is.EqualTo(new PlaybackTrack(name, "track-01.bin")));
        Assert.That(Text(cue).Split('\n').Select(Encoding.UTF8.GetByteCount), Has.All.LessThanOrEqualTo(PlaybackCue.MaxLineBytes));
    }

    [Test]
    public void Playback_Latin1AndBomInput_AreDecoded()
    {
        const string text = "FILE \"Pokémon (Track 1).bin\" BINARY\n TRACK 01 MODE2/2352\n INDEX 01 00:00:00";
        string[] stored = ["Pokémon (Track 1).bin"];

        Assert.That(CueSheet.ForPlayback(Encoding.Latin1.GetBytes(text), "Pokémon.cue", stored).Error, Is.Null);
        Assert.That(CueSheet.ForPlayback([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)], "Pokémon.cue", stored).Error, Is.Null);
    }

    [Test]
    public void Playback_NinetyNineTracks_Parse()
    {
        var text = string.Concat(Enumerable.Range(1, 99).Select(i => $"FILE \"t{i}.bin\" BINARY\n TRACK {i:00} AUDIO\n INDEX 01 00:00:00\n"));
        var cue = Playable(text, Enumerable.Range(1, 99).Select(i => $"t{i}.bin").ToList());

        Assert.That(cue.Tracks[^1], Is.EqualTo(new PlaybackTrack("t99.bin", "track-99.bin")));
    }

    [Test]
    public void Playback_RejectsACueOverTheIngestBound()
    {
        var text = TwoTrackCue + "\nREM " + new string('x', (int)IngestSetPlanner.MaxCueBytes);

        Assert.That(Error(text, TwoTracks), Does.Contain("larger than"));
    }

    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00\nFILE \"A.BIN\" BINARY\n TRACK 02 AUDIO\n INDEX 01 00:00:00", "repeated")]
    [TestCase("FILE \"a.bin\" WAVE\n TRACK 01 AUDIO\n INDEX 01 00:00:00", "BINARY")]
    [TestCase("FILE \"a.bin\"\n TRACK 01 AUDIO\n INDEX 01 00:00:00", "BINARY")]
    [TestCase("FILE \"a.bin\" BINARY extra\n TRACK 01 AUDIO\n INDEX 01 00:00:00", "BINARY")]
    [TestCase("FILE\n TRACK 01 AUDIO", "no name")]
    [TestCase("FILE \"a.bin BINARY", "closing quote")]
    [TestCase(" TRACK 01 AUDIO\nFILE \"a.bin\" BINARY", "before the first FILE")]
    [TestCase("FILE \"a.bin\" BINARY\nFILE \"b.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00", "has no TRACK")]
    [TestCase("FILE \"a.bin\" BINARY", "has no TRACK")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO", "no INDEX 01")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 00 00:00:00\n TRACK 02 AUDIO\n INDEX 01 00:00:00", "no INDEX 01")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 02 AUDIO\n INDEX 01 00:00:00", "out of order")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00\n TRACK 03 AUDIO\n INDEX 01 00:00:00", "out of order")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00\n TRACK 01 AUDIO\n INDEX 01 00:00:00", "out of order")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 100 AUDIO\n INDEX 01 00:00:00", "malformed TRACK")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01\n INDEX 01 00:00:00", "malformed TRACK")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO extra\n INDEX 01 00:00:00", "malformed TRACK")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK x1 AUDIO\n INDEX 01 00:00:00", "malformed TRACK")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 MODE1/2048\n INDEX 01 00:00:00", "mode")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 MODE2/2336\n INDEX 01 00:00:00", "mode")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 CDG\n INDEX 01 00:00:00", "mode")]
    [TestCase("FILE \"a.bin\" BINARY\n INDEX 01 00:00:00", "INDEX outside a TRACK")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:60:00", "malformed INDEX")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:75", "malformed INDEX")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 100:00:00", "malformed INDEX")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00", "malformed INDEX")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01", "malformed INDEX")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00 extra", "malformed INDEX")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00\n INDEX 02 00:01:00", "INDEX 00 followed by one INDEX 01")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00\n INDEX 00 00:01:00", "INDEX 00 followed by one INDEX 01")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00\n INDEX 01 00:01:00", "INDEX 00 followed by one INDEX 01")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00\n PREGAP 00:02:00", "one PREGAP, before INDEX 01")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n PREGAP 00:02:00\n PREGAP 00:02:00\n INDEX 01 00:00:00", "one PREGAP, before INDEX 01")]
    [TestCase("FILE \"a.bin\" BINARY\n TRACK 01 AUDIO\n PREGAP 2s\n INDEX 01 00:00:00", "malformed PREGAP")]
    [TestCase("FILE \"a.bin\" BINARY\n PREGAP 00:02:00", "PREGAP outside a TRACK")]
    [TestCase("REM nothing", "no FILE line")]
    public void Playback_RejectsMalformedCommands(string text, string error) =>
        Assert.That(Error(text, OnlyReferenced(text)), Does.Contain(error));

    /// <summary>The stored set for a malformed-command case: just the names its FILE lines use, so coverage isn't the error.</summary>
    private static string[] OnlyReferenced(string text) => text.Contains("\"b.bin\"") ? ["a.bin", "b.bin"] : ["a.bin"];

    [Test]
    public void Playback_RejectsAFileThatIsNotAStoredTrack() =>
        Assert.That(Error("FILE \"other.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00", ["a.bin"]), Does.Contain("not a stored track"));

    [Test]
    public void Playback_RejectsAnUnreferencedStoredTrack() =>
        Assert.That(Error(TwoTrackCue, [.. TwoTracks, "Game (USA) (Track 3).bin"]), Does.Contain("(Track 3).bin"));

    [Test]
    public void Playback_RejectsStoredTracksThatCollideIgnoringCase() =>
        Assert.That(Error(TwoTrackCue, [.. TwoTracks, "GAME (USA) (TRACK 1).BIN"]), Does.Contain("unique"));

    [Test]
    public void Playback_RejectsAnUnsafeFileName() =>
        Assert.That(Error("FILE \"../a.bin\" BINARY\n TRACK 01 AUDIO\n INDEX 01 00:00:00", ["a.bin"]), Does.Contain("../a.bin"));
}
