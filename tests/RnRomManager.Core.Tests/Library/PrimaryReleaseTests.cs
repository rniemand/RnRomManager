using RnRomManager.Core.Dats;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Spec §6.1 and T6: one primary release; a titledb game ranks base releases ahead of updates and DLC.</summary>
public class PrimaryReleaseTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    private static InfoJsonRom Rom(string fileName, IReadOnlyList<string>? regions = null, string? revision = null, params string[] flags) => new()
    {
        FileName = fileName,
        OriginalFileName = fileName,
        BaseTitle = "Game",
        Regions = regions ?? [],
        Languages = [],
        Revision = revision,
        Flags = flags,
        IsPrimary = false,
        Identification = new InfoJsonIdentification(IdentificationMethod.TitleDb, "titledb", "2026.11.10", "Game [0100000000002000]"),
        Metadata = new InfoJsonRomMetadata(1, "00000000", new string('a', 32), new string('b', 40), When, When, "/ingest/x"),
    };

    private static readonly InfoJsonRom HackBase = Rom("Game (Hack) [0100000000002000][v0].nsp", flags: "Hack");
    private static readonly InfoJsonRom Update = Rom("Game [0100000000002800][v65536].nsp", revision: "v65536", flags: "Update");
    private static readonly InfoJsonRom Dlc = Rom("Game (DLC) [0100000000003001][v0].nsp", flags: "DLC");

    private static InfoJsonRom PrimaryOf(IReadOnlyList<InfoJsonRom> releases, DatSource source) =>
        PrimaryRelease.Apply(releases, source).Single(r => r.IsPrimary);

    [Test]
    public void TitleDb_AHackFlaggedBase_OutranksUpdatesAndDlc()
    {
        Assert.That(PrimaryOf([Update, Dlc, HackBase], DatSource.TitleDb).FileName, Is.EqualTo(HackBase.FileName));
        Assert.That(PrimaryOf([Update, Dlc, HackBase], DatSource.Libretro).FileName, Is.Not.EqualTo(HackBase.FileName),
            "the comparer alone puts a Hack last");
    }

    [Test]
    public void TitleDb_OnlyAddOns_GetExactlyOnePrimary_ByTheComparer()
    {
        var applied = PrimaryRelease.Apply([Dlc, Update], DatSource.TitleDb);

        Assert.That(applied.Count(r => r.IsPrimary), Is.EqualTo(1));
        Assert.That(applied.Single(r => r.IsPrimary).FileName, Is.EqualTo(PrimaryReleaseRanker.Primary([Dlc, Update], PrimaryRelease.Rank).FileName));
    }

    private static InfoJsonRom WithSha1(InfoJsonRom rom, char c) => rom with { Metadata = rom.Metadata with { Sha1 = new string(c, 40) } };

    [Test]
    public void ChosenSha1_WinsWhileListed_ElseTheRankerPicks()
    {
        var japan = WithSha1(Rom("Tetris (Japan).gb", ["Japan"]), 'j');
        var world = WithSha1(Rom("Tetris (World).gb", ["World"]), 'w');

        Assert.That(PrimaryRelease.Apply([world, japan], DatSource.Libretro, japan.Metadata.Sha1).Where(r => r.IsPrimary).Select(r => r.FileName),
            Is.EqualTo(new[] { japan.FileName }));
        Assert.That(PrimaryRelease.Apply([world, japan], DatSource.Libretro).Single(r => r.IsPrimary).FileName, Is.EqualTo(world.FileName));
        Assert.That(PrimaryRelease.Apply([world], DatSource.Libretro, japan.Metadata.Sha1).Single(r => r.IsPrimary).FileName, Is.EqualTo(world.FileName),
            "a chosen release that left the game falls back to the ranker");
    }

    [Test]
    public void TitleDb_Order_PutsTheBaseFirst()
    {
        Assert.That(PrimaryRelease.Order([Dlc, Update, HackBase], DatSource.TitleDb).First().FileName, Is.EqualTo(HackBase.FileName));
    }

    private static readonly InfoJsonRom[] Embedded =
    [
        Rom("Tetris (Japan).gb", ["Japan"]),
        Rom("Tetris (World) (Rev 1).gb", ["World"], "Rev 1"),
        Rom("Tetris (World).gb", ["World"]),
        Rom("Tetris (World) (Hack).gb", ["World"], flags: "Hack"),
        Rom("Tetris (USA) (Beta).gb", ["USA"], flags: "Beta"),
    ];

    [Test]
    public void Libretro_KeepsTheExistingRanking()
    {
        // The old system-less Apply was PrimaryReleaseRanker.Primary over the same Rank; its order was OrderBy that comparer.
        Assert.That(PrimaryOf(Embedded, DatSource.Libretro).FileName, Is.EqualTo(PrimaryReleaseRanker.Primary(Embedded, PrimaryRelease.Rank).FileName));
        Assert.That(PrimaryRelease.Order(Embedded, DatSource.Libretro).Select(r => r.FileName),
            Is.EqualTo(Embedded.OrderBy(PrimaryRelease.Rank, PrimaryReleaseRanker.Instance).Select(r => r.FileName)));
        // A Hack doesn't move ahead of an Update flag outside titledb.
        Assert.That(PrimaryOf([Update, HackBase], DatSource.Libretro).FileName,
            Is.EqualTo(PrimaryReleaseRanker.Primary([Update, HackBase], PrimaryRelease.Rank).FileName));
    }

    private static InfoJsonRom Set(string name, string? cloneOf, params string[] regions) =>
        Rom(name + ".mamez", regions) with
        {
            Header = new System.Text.Json.Nodes.JsonObject { ["setName"] = name, ["cloneOf"] = cloneOf },
            Metadata = Rom(name).Metadata with { Sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(name))) },
        };

    [Test]
    public void Mame_ParentsRankAheadOfClones_ThenTheRanker()
    {
        var puckman = Set("puckman", null);
        var pacman = Set("pacman", "puckman", "World");
        var manual = Rom("Pac Fan (Unverified).mamez"); // no header: counts as a parent

        Assert.That(PrimaryOf([pacman, puckman], DatSource.Mame).FileName, Is.EqualTo("puckman.mamez"));
        Assert.That(PrimaryRelease.Order([pacman, manual, puckman], DatSource.Mame).Last().FileName, Is.EqualTo("pacman.mamez"));
        Assert.That(PrimaryRelease.Apply([pacman, puckman], DatSource.Mame, pacman.Metadata.Sha1).Single(r => r.IsPrimary).FileName,
            Is.EqualTo("pacman.mamez"), "an Admin's choice still wins");
    }

    // FBNeo D1: an Fbneo system orders parents first, as Mame.
    [Test]
    public void Fbneo_ParentsRankAheadOfClones()
    {
        var puckman = Set("puckman", null);
        var pacman = Set("pacman", "puckman", "World");

        Assert.That(PrimaryOf([pacman, puckman], DatSource.Fbneo).FileName, Is.EqualTo("puckman.mamez"));
    }
}
