using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using RnRomManager.TestData;

namespace RnRomManager.Systems.NintendoDs.Tests;

/// <summary>The committed runnable DS cartridge (Fixtures/nds-marker): its pinned bytes, its header and its E2E DAT entry.</summary>
public class NdsMarkerFixtureTests
{
    [Test]
    public void CommittedCartridge_MatchesItsPinnedSha1()
    {
        Assert.That(Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(TestFixtures.NdsMarkerPath))), Is.EqualTo(TestFixtures.NdsMarkerSha1));
    }

    [Test]
    public void Header_IsPlausible_WithTheMarkerFields()
    {
        var header = new NintendoDsHandler().ReadHeader(File.ReadAllBytes(TestFixtures.NdsMarkerPath))!;

        Assert.That((header.ClassifiedSystemId, header.Title, header.IsPlausible), Is.EqualTo(("nds", "NDS MARKER", true)));
        Assert.That((header.Fields["gameCode"], header.Fields["makerCode"], header.Fields["headerChecksumValid"]),
            Is.EqualTo(((object?)"NMKE", (object?)"00", (object?)true)));
    }

    [Test]
    public void NdsDat_ListsTheStagedCartridge_UnderItsE2EName()
    {
        using var dir = new TempDir();
        var staged = dir.Combine("staged", TestFixtures.NdsMarkerStagedName);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(TestFixtures.NdsMarkerPath, staged);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = TestDataCommand.Run(["--ingest", dir.Combine("ingest"), "--cache", dir.Combine("cache"), "--set", "Basic", "--nds-dat", staged],
            output, error, new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)));

        Assert.That(exit, Is.Zero, error.ToString());
        var dat = File.ReadAllText(dir.Combine("cache", "dats", "no-intro", IngestSeeder.NintendoDsDat + ".dat"));
        Assert.That(dat, Does.Contain("name \"Marker Test (World)\""));
        Assert.That(dat, Does.Contain($"rom ( name \"{TestFixtures.NdsMarkerStagedName}\" size {new FileInfo(staged).Length} "));
        Assert.That(dat, Does.Contain($"sha1 {TestFixtures.NdsMarkerSha1.ToUpperInvariant()} )"));
    }
}
