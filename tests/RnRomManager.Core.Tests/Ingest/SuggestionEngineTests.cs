using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;

namespace RnRomManager.Core.Tests.Ingest;

public class SuggestionEngineTests
{
    private static DatRom Rom(string title, long size = 10, string? name = null) =>
        new(title, name ?? title + ".gb", size, "12345678", new string('a', 32), Guid.NewGuid().ToString("N").PadRight(40, '0'));

    private static DatStore Store(params DatRom[] roms) => new(new NoIntroDat("GB", "v1", roms), []);

    [TestCase("  tEtRiS!!  2 ", "TETRIS 2")]
    [TestCase("Pokémon: Red-Blue", "POKMON REDBLUE")]
    [TestCase(null, "")]
    public void Normalization(string? input, string expected) => Assert.That(SuggestionEngine.Normalize(input), Is.EqualTo(expected));

    [Test]
    public void JaccardUsesDistinctTokens_AndIgnoresOrder()
    {
        var result = SuggestionEngine.Find(Store(Rom("Red Blue Green (World)")), "green blue red red", "x.gb", 1);
        Assert.That(result.Single().Score, Is.EqualTo(1));
    }

    [Test]
    public void LevenshteinUsesSpaceFreeStrings()
    {
        var result = SuggestionEngine.Find(Store(Rom("Tetris (World)")), "TETRIX", "x.gb", 1);
        Assert.That(result.Single().Score, Is.EqualTo(5d / 6).Within(1e-12));
        Assert.That(SuggestionEngine.Find(Store(Rom("Tetris (World)")), "TET RIS", "x.gb", 1).Single().Score, Is.EqualTo(1));
    }

    [Test]
    public void ThresholdIncludesPointFour_AndIsAppliedAfterBoost()
    {
        var store = Store(Rom("ABXYZ (World)"), Rom("AXYZW (World)"));
        Assert.That(SuggestionEngine.Find(store, "ABCDE", "!.gb", 1).Select(s => s.DatGameName), Is.EqualTo(new[] { "ABXYZ (World)" }));
        Assert.That(SuggestionEngine.Find(Store(Rom("ABCXXXXXXX (World)")), "ABCDEFGHIJ", "!.gb", 10).Single().Score, Is.EqualTo(0.4).Within(1e-12));
    }

    [TestCase("WRONG", "Tetris (USA) [hack].gb")]
    [TestCase("TETRIS", "wrong [Tetris].gb")]
    public void HigherInputWins_AndFilenameTagsAreCut(string header, string filename) =>
        Assert.That(SuggestionEngine.Find(Store(Rom("Tetris (World)")), header, filename, 1).Single().Score, Is.EqualTo(1));

    [Test]
    public void EmptyInputs_DoNotReceiveSizeOnlySuggestions()
    {
        Assert.That(SuggestionEngine.Find(Store(Rom("Tetris (World)")), null, "!!!.gb", 10), Is.Empty);
        Assert.That(SuggestionEngine.Find(null, "Tetris", "Tetris.gb", 10), Is.Empty);
    }

    [Test]
    public void DistinctTitles_SelectBestReleaseAfterBoost_ThenRomName()
    {
        var store = Store(Rom("Tetris (Japan)", 11, "a.gb"), Rom("Tetris (World)", 10, "z.gb"),
            Rom("Tetris (USA)", 10, "b.gb"));
        var result = SuggestionEngine.Find(store, "TETRIS", "x.gb", 10);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.Single(), Is.EqualTo(new Suggestion("Tetris (USA)", "b.gb", 1.1)));
    }

    [Test]
    public void TopFiveAreDistinctTitles_OrderedByScoreThenRomName()
    {
        var store = Store(Enumerable.Range(1, 7).Reverse().Select(i => Rom($"Tetris {i} (World)")).ToArray());
        Assert.That(SuggestionEngine.Find(store, "TETRIS", "x.gb", 1).Select(s => s.RomName),
            Is.EqualTo(Enumerable.Range(1, 5).Select(i => $"Tetris {i} (World).gb")));
    }
}
