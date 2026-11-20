using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Tests.Naming;

public class ReleaseFileNameTests
{
    private static readonly ReleaseTags None = new([], [], null, false, false, null);

    private static string Edit(string fileName, string baseTitle, ReleaseTags before, Func<ReleaseTags, ReleaseTags> change) =>
        ReleaseFileName.Edit(fileName, baseTitle, before, change(before));

    [Test]
    public void SpecExamples_B7()
    {
        var tetris = None with { Regions = ["World"], Revision = "Rev 1" };
        Assert.That(Edit("Tetris (World) (Rev 1).gb", "Tetris", tetris, t => t with { Hack = true }), Is.EqualTo("Tetris (World) (Rev 1) (Hack).gb"));
        Assert.That(Edit("Puzzle Land (Hack).gb", "Puzzle Land", None with { Hack = true }, t => t with { Hack = false }), Is.EqualTo("Puzzle Land.gb"));

        var update = None with { Version = "1.0.1" };
        const string orbit = "Orbit [0100000000003800][v65536][1.0.1].nsp";
        Assert.That(Edit(orbit, "Orbit", update, t => t with { Version = "1.0.2" }), Is.EqualTo("Orbit [0100000000003800][v65536][1.0.2].nsp"));
        Assert.That(Edit(orbit, "Orbit", update, t => t with { Version = null }), Is.EqualTo("Orbit [0100000000003800][v65536].nsp"));
        Assert.That(Edit("Orbit [0100000000003800][v0].nsp", "Orbit", None, t => t with { Version = "1.0.1" }),
            Is.EqualTo("Orbit [0100000000003800][v0][1.0.1].nsp"));
    }

    [Test]
    public void UnchangedFields_LeaveTheNameAlone()
    {
        var tags = None with { Regions = ["USA"], Hack = true };
        Assert.That(ReleaseFileName.Edit("weird  name..(Hack).gb", "X", tags, tags), Is.EqualTo("weird  name..(Hack).gb"));
        // Attach-style name: only the changed group is touched.
        Assert.That(Edit("my source (Hack).gb", "Game", tags, t => t with { Translated = true }), Is.EqualTo("my source (Hack) (Translated).gb"));
    }

    [Test]
    public void FoundGroup_IsReplaced_IgnoringCase()
    {
        var tags = None with { Regions = ["USA", "Europe"], Languages = ["En", "Fr"], Revision = "Rev 1" };
        const string name = "Game (usa, europe) (En,Fr) (Rev 1).gb";
        Assert.That(Edit(name, "Game", tags, t => t with { Regions = ["Japan"] }), Is.EqualTo("Game (Japan) (En,Fr) (Rev 1).gb"));
        Assert.That(Edit(name, "Game", tags, t => t with { Languages = ["De"] }), Is.EqualTo("Game (usa, europe) (De) (Rev 1).gb"));
        Assert.That(Edit(name, "Game", tags, t => t with { Revision = "Rev 2" }), Is.EqualTo("Game (usa, europe) (En,Fr) (Rev 2).gb"));
    }

    [Test]
    public void FoundGroup_IsRemoved_WithItsPrecedingSpace()
    {
        var tags = None with { Regions = ["USA"], Languages = ["En"], Revision = "Rev 1" };
        const string name = "Game (USA) (En) (Rev 1).gb";
        Assert.That(Edit(name, "Game", tags, t => t with { Regions = [] }), Is.EqualTo("Game (En) (Rev 1).gb"));
        Assert.That(Edit(name, "Game", tags, t => t with { Languages = [] }), Is.EqualTo("Game (USA) (Rev 1).gb"));
        Assert.That(Edit(name, "Game", tags, t => t with { Revision = null }), Is.EqualTo("Game (USA) (En).gb"));
    }

    [Test]
    public void NewGroup_GoesBeforeTheFirstBracketGroup_OrAtTheEnd()
    {
        Assert.That(Edit("Orbit [0100000000003800][v0].nsp", "Orbit", None, t => t with { Hack = true }), Is.EqualTo("Orbit (Hack) [0100000000003800][v0].nsp"));
        Assert.That(Edit("Orbit[0100000000003800].nsp", "Orbit", None, t => t with { Languages = ["En"] }), Is.EqualTo("Orbit (En)[0100000000003800].nsp"));
        Assert.That(Edit("Game (USA).gb", "Game", None with { Regions = ["USA"] }, t => t with { Revision = "Rev A" }), Is.EqualTo("Game (USA) (Rev A).gb"));
    }

    [Test]
    public void NewRegionGroup_GoesAfterTheBaseTitle_ElseAtTheDefaultPosition()
    {
        Assert.That(Edit("game (Rev 1) (Hack).gb", "Game", None with { Revision = "Rev 1", Hack = true }, t => t with { Regions = ["USA"] }),
            Is.EqualTo("game (USA) (Rev 1) (Hack).gb"));
        Assert.That(Edit("source dump [x].gb", "Game", None, t => t with { Regions = ["USA"] }), Is.EqualTo("source dump (USA) [x].gb"));
        Assert.That(Edit("source dump.gb", "Game", None, t => t with { Regions = ["USA"] }), Is.EqualTo("source dump (USA).gb"));
    }

    [Test]
    public void Result_IsSanitized_AndKeepsTheExtension()
    {
        Assert.That(Edit("Game.GB", "Game", None, t => t with { Revision = "Rev: 1" }), Is.EqualTo("Game (Rev_ 1).GB"));
        var longStem = new string('a', FileNameSanitizer.MaxLength - 2);
        Assert.That(Edit(longStem + ".gb", "x", None, t => t with { Hack = true }).Length, Is.EqualTo(FileNameSanitizer.MaxLength + 3));
    }
}
