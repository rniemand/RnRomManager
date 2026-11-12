using System.Text;
using RnRomManager.Core.Ingest;

namespace RnRomManager.Core.Tests.Ingest;

/// <summary>Disc systems B7: planning bin/cue sets from one folder's loose files.</summary>
public class IngestSetPlannerTests
{
    private static PlannerFile Cue(string name, params string[] files) =>
        CueText(name, string.Concat(files.Select(f => $"FILE \"{f}\" BINARY\n  TRACK 01 MODE2/2352\n")));

    private static PlannerFile CueText(string name, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new PlannerFile(name, bytes.Length, bytes);
    }

    private static PlannerFile File(string name, long size = 2352) => new(name, size);

    private static IReadOnlyList<PlannedUnit> Plan(IReadOnlySet<string>? selected, params PlannerFile[] files) => IngestSetPlanner.Plan(files, selected);

    private static IReadOnlyList<PlannedUnit> Plan(params PlannerFile[] files) => Plan(null, files);

    private static PlannedUnit Set(params string[] members) => new(PlannedKind.Set, members);

    private static PlannedUnit Lone(string name) => new(PlannedKind.Lone, [name]);

    private static PlannedUnit Skipped(string reason, params string[] members) => new(PlannedKind.Skipped, members, reason);

    private static void AssertUnits(IReadOnlyList<PlannedUnit> actual, params PlannedUnit[] expected) =>
        Assert.That(actual.Select(Describe), Is.EqualTo(expected.Select(Describe)));

    private static string Describe(PlannedUnit u) => $"{u.Kind} {u.Reason} [{string.Join(" | ", u.Members)}]";

    [Test]
    public void CueAndTwoTracks_AreOneSet_CueFirst_ThenCueOrder()
    {
        var units = Plan(File("Game (Track 2).bin"), Cue("Game.cue", "Game (Track 1).bin", "Game (Track 2).bin"), File("Game (Track 1).bin"));

        AssertUnits(units, Set("Game.cue", "Game (Track 1).bin", "Game (Track 2).bin"));
    }

    [Test]
    public void MissingMember_EveryPresentMemberIsIncompleteSet()
    {
        var units = Plan(Cue("Game.cue", "Game (Track 1).bin", "Game (Track 2).bin"), File("Game (Track 1).bin"));

        AssertUnits(units, Skipped(IngestReason.IncompleteSet, "Game.cue", "Game (Track 1).bin"));
    }

    [Test]
    public void LoneCue_WhoseFilesAreMissing_IsIncompleteSet()
    {
        AssertUnits(Plan(Cue("Game.cue", "Game.bin")), Skipped(IngestReason.IncompleteSet, "Game.cue"));
    }

    [TestCase("Game (Track 2).wav")]
    [TestCase("Game.iso")]
    [TestCase("Game.cue")]
    public void CueNamingANonBinFile_OrItself_IsUnsupportedSetMember(string member)
    {
        var units = Plan(Cue("Game.cue", "Game (Track 1).bin", member), File("Game (Track 1).bin"), File("Game (Track 2).wav"), File("Game.iso"));

        Assert.That(units[0].Kind, Is.EqualTo(PlannedKind.Skipped));
        Assert.That(units[0].Reason, Is.EqualTo(IngestReason.UnsupportedSetMember));
        Assert.That(units[0].Members, Does.Contain("Game.cue").And.Contain("Game (Track 1).bin"));
    }

    [Test]
    public void CueWhoseOwnNameIsNotPortable_IsUnsupportedSetMember()
    {
        var units = Plan(Cue("Game: Two.cue", "Game.bin"), File("Game.bin"));

        AssertUnits(units, Skipped(IngestReason.UnsupportedSetMember, "Game: Two.cue", "Game.bin"));
    }

    [Test]
    public void UnparsableOrUnreadableCue_AndBinsStartingWithItsStem_AreIncomplete_OthersAreLone()
    {
        var units = Plan(
            CueText("Game.cue", "REM no files"),
            new PlannerFile("Other.cue", 100, null), // couldn't be read
            File("Game (Track 1).bin"), File("GAME (Track 2).bin"), File("Other (Track 1).bin"), File("Unrelated.bin"));

        AssertUnits(units,
            Skipped(IngestReason.IncompleteSet, "GAME (Track 2).bin", "Game (Track 1).bin", "Game.cue"),
            Skipped(IngestReason.IncompleteSet, "Other (Track 1).bin", "Other.cue"),
            Lone("Unrelated.bin"));
    }

    [Test]
    public void BinNamedByAParsedCue_StaysInThatSet_EvenUnderABrokenCuesStem()
    {
        var units = Plan(CueText("Game.cue", "nothing"), Cue("Game 2.cue", "Game 2 (Track 1).bin"), File("Game 2 (Track 1).bin"), File("Game (Track 1).bin"));

        // Ordinal order: "Game (" sorts before "Game 2".
        AssertUnits(units,
            Skipped(IngestReason.IncompleteSet, "Game (Track 1).bin", "Game.cue"),
            Set("Game 2.cue", "Game 2 (Track 1).bin"));
    }

    [Test]
    public void Units_ComeInNameOrder_ByTheirFirstMember()
    {
        var units = Plan(File("Zed.iso"), Cue("Game.cue", "Game (Track 1).bin"), File("Game (Track 1).bin"), File("Alpha.iso"), File("Game.txt"));

        // "Game (Track 1).bin" sorts before "Game.cue", so the set runs at its position.
        AssertUnits(units, Lone("Alpha.iso"), Set("Game.cue", "Game (Track 1).bin"), Lone("Game.txt"), Lone("Zed.iso"));
    }

    [Test]
    public void TwoCuesNamingOneBin_AreBothSetConflict()
    {
        var units = Plan(Cue("A.cue", "Shared.bin", "A (Track 2).bin"), Cue("B.cue", "SHARED.bin"), File("Shared.bin"), File("A (Track 2).bin"));

        AssertUnits(units,
            Skipped(IngestReason.SetConflict, "A.cue", "Shared.bin", "A (Track 2).bin"),
            Skipped(IngestReason.SetConflict, "B.cue"));
    }

    [Test]
    public void NamedFileMatchingTwoFilesThatDifferOnlyByCase_IsSetConflict()
    {
        var units = Plan(Cue("Game.cue", "Game.bin"), File("Game.bin"), File("GAME.BIN"));

        AssertUnits(units, Skipped(IngestReason.SetConflict, "Game.cue", "GAME.BIN", "Game.bin"));
    }

    [Test]
    public void CueOver64KiB_IsALoneFile()
    {
        var units = Plan(new PlannerFile("Big.cue", IngestSetPlanner.MaxCueBytes + 1), File("Big.bin"));

        AssertUnits(units, Lone("Big.bin"), Lone("Big.cue"));
    }

    [Test]
    public void Names_ResolveIgnoringCase_KeepingTheDiskSpelling()
    {
        var units = Plan(Cue("Game.cue", "game (track 1).BIN"), File("Game (Track 1).bin"));

        AssertUnits(units, Set("Game.cue", "Game (Track 1).bin"));
    }

    [Test]
    public void SelectingOneBin_SelectsTheWholeSet_AndUnselectedLoneFilesAreLeftOut()
    {
        var units = Plan(new HashSet<string> { "Game (Track 2).bin" },
            Cue("Game.cue", "Game (Track 1).bin", "Game (Track 2).bin"), File("Game (Track 1).bin"), File("Game (Track 2).bin"), File("Other.iso"));

        AssertUnits(units, Set("Game.cue", "Game (Track 1).bin", "Game (Track 2).bin"));
    }
}
