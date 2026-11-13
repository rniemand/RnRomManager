using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Tests.Naming;

/// <summary>Spec §6.1 base title and §6.1.1 tag parsing, on real DAT-style names.</summary>
public class NoIntroNameTests
{
    [TestCase("Tetris (World) (Rev 1)", "Tetris")]
    [TestCase("Yelu Wangzi (Prince Yeh Rude) (Taiwan)", "Yelu Wangzi (Prince Yeh Rude)")]
    [TestCase("Legend of Zelda, The - Link's Awakening (USA, Europe) (Rev 2)", "Legend of Zelda, The - Link's Awakening")]
    [TestCase("Maus, Die (Germany)", "Maus, Die")]
    [TestCase("seaQuest DSV (USA, Europe)", "seaQuest DSV")]
    [TestCase("Tetris", "Tetris")]
    [TestCase("Mystery Game (Proto) (Top Secret)", "Mystery Game")] // no region group: up to the first " ("
    [TestCase("Pocket Monsters - Aka (Japan) (SGB Enhanced)", "Pocket Monsters - Aka")]
    [TestCase("4 in 1 (Europe) (4B-001, Sachen) (Unl)", "4 in 1")]
    [TestCase("Game (Hong Kong, United Kingdom)", "Game")]
    public void BaseTitle(string name, string expected) =>
        Assert.That(NoIntroName.Parse(name).BaseTitle, Is.EqualTo(expected));

    // B3a: Redump's region names.
    [TestCase("Foo (UK)", new[] { "UK" })]
    [TestCase("Foo (UK, Australia)", new[] { "UK", "Australia" })]
    [TestCase("Foo (United Arab Emirates)", new[] { "United Arab Emirates" })]
    public void RedumpRegions_AreRegionsNotFlags(string text, string[] regions)
    {
        var name = NoIntroName.Parse(text);

        Assert.That((name.BaseTitle, name.Flags.Count), Is.EqualTo(("Foo", 0)));
        Assert.That(name.Regions, Is.EqualTo(regions));
    }

    [Test]
    public void AllTagKinds_LeftToRight()
    {
        var name = NoIntroName.Parse("Legend of Zelda, The - Link's Awakening DX (USA, Europe) (En,Fr,De) (Rev 2) (SGB Enhanced, GB Compatible) (Proto 2)");

        Assert.That(name.Regions, Is.EqualTo(new[] { "USA", "Europe" }));
        Assert.That(name.Languages, Is.EqualTo(new[] { "En", "Fr", "De" }));
        Assert.That(name.Revision, Is.EqualTo("Rev 2"));
        Assert.That(name.Flags, Is.EqualTo(new[] { "SGB Enhanced, GB Compatible", "Proto 2" }), "flags are never split");
    }

    [TestCase("Game (Japan) (v1.0.3)", "v1.0.3")]
    [TestCase("Game (Japan) (v2)", "v2")]
    [TestCase("Game (Japan) (v1.6b)", "v1.6b")]
    [TestCase("Game (Japan) (Rev A)", "Rev A")]
    [TestCase("Game (Japan) (Rev 1.1)", "Rev 1.1")]
    public void Revision(string name, string expected) =>
        Assert.That(NoIntroName.Parse(name).Revision, Is.EqualTo(expected));

    [Test]
    public void OnlyTheFirstRevisionCounts_LaterOnesAreFlags()
    {
        var name = NoIntroName.Parse("Game (USA) (Rev 1) (v1.2)");

        Assert.That((name.Revision, name.Flags.Single()), Is.EqualTo(("Rev 1", "v1.2")));
    }

    [TestCase("Game (World) (v1.2.3E)")] // capital suffix: not the §6.1.1 version pattern
    [TestCase("Game (World) (Rev)")]
    public void RevisionLookalikes_AreFlags(string text)
    {
        var name = NoIntroName.Parse(text);

        Assert.That(name.Revision, Is.Null);
        Assert.That(name.Flags, Has.Count.EqualTo(1));
    }

    [Test]
    public void RomNameAddsTags_TheGameNameDoesNot()
    {
        // §6.1.1: tags come from the ROM name; it can carry more than the game name.
        var game = NoIntroName.Parse("Some Homebrew (World)");
        var rom = NoIntroName.Parse("Some Homebrew (World) (Aftermarket) (Unl)");

        Assert.That(game.BaseTitle, Is.EqualTo(rom.BaseTitle));
        Assert.That(game.Flags, Is.Empty);
        Assert.That(rom.Flags, Is.EqualTo(new[] { "Aftermarket", "Unl" }));
    }

    [Test]
    public void NoRegionGroup_EveryGroupIsATag()
    {
        var name = NoIntroName.Parse("Mystery Game (En) (Proto)");

        Assert.That((name.BaseTitle, name.Regions.Count), Is.EqualTo(("Mystery Game", 0)));
        Assert.That(name.Languages, Is.EqualTo(new[] { "En" }));
        Assert.That(name.Flags, Is.EqualTo(new[] { "Proto" }));
    }

    [Test]
    public void NonRegionGroupBeforeTheRegion_IsTitleNotTag()
    {
        var name = NoIntroName.Parse("Yelu Wangzi (Prince Yeh Rude) (Taiwan) (Unl)");

        Assert.That(name.Regions, Is.EqualTo(new[] { "Taiwan" }));
        Assert.That(name.Flags, Is.EqualTo(new[] { "Unl" }));
    }

    [Test]
    public void PartlyKnownRegionGroup_IsNotTheRegionGroup()
    {
        var name = NoIntroName.Parse("Game (USA, Atlantis) (Europe)");

        Assert.That((name.BaseTitle, name.Regions.Single()), Is.EqualTo(("Game (USA, Atlantis)", "Europe")));
    }

    [Test]
    public void RegionList_HoldsEveryRegionInTheGbAndGbcDats()
    {
        string[] seen = ["Japan", "USA", "Europe", "World", "Taiwan", "Germany", "France", "Spain", "China", "Australia",
            "Unknown", "Hong Kong", "Italy", "Sweden", "Canada", "United Kingdom", "Korea", "Brazil", "Netherlands"];

        Assert.That(NoIntroRegions.All, Is.SupersetOf(seen));
    }
}
