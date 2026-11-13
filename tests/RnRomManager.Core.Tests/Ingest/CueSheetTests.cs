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
}
