using RnRomManager.Core.Library;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Spec §14.1 derived columns: <c>bucket</c>, <c>region_key</c>, <c>has_default_boxart</c>, whole-second mtime.</summary>
public class MirrorEntryTests
{
    [TestCase("gb/#/1942.zip", "#")]
    [TestCase("gb/S/seaQuest DSV.zip", "S")]
    [TestCase("Tetris.zip", "T")]
    public void Bucket_ComesFromTheZipName(string zipPath, string bucket)
    {
        var (game, _, _, _) = new MirrorEntry(Game("x", Rom("a.gb", RomBytes(1))), zipPath, 1, DateTime.UtcNow).ToRows();
        Assert.That(game.Bucket, Is.EqualTo(bucket));
    }

    [Test]
    public void RegionKey_PipeAtBothEnds_EmptyIsSinglePipe()
    {
        var info = Game("x", Rom("a.gb", RomBytes(1), regions: ["USA", "Europe"]), Rom("b.gb", RomBytes(2), isPrimary: false, regions: []));

        var (_, roms, _, _) = new MirrorEntry(info, "gb/X/x.zip", 1, DateTime.UtcNow).ToRows();

        Assert.That(roms.Select(r => r.RegionKey), Is.EqualTo(new[] { "|USA|Europe|", "|" }));
    }

    [Test]
    public void NoBoxArt_HasDefaultBoxArtFalse_MtimeTruncated()
    {
        var mtime = new DateTime(2026, 11, 4, 1, 2, 3, DateTimeKind.Utc).AddTicks(9_999_999);

        var (game, _, _, _) = new MirrorEntry(Game("x", Rom("a.gb", RomBytes(1))), "gb/X/x.zip", 1, mtime).ToRows();

        Assert.That(game.HasDefaultBoxArt, Is.False);
        Assert.That(game.ZipMtimeUtc, Is.EqualTo(new DateTime(2026, 11, 4, 1, 2, 3, DateTimeKind.Utc)));
    }
}
