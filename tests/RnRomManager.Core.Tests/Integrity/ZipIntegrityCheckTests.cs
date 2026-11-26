using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Integrity;

/// <summary>§12.2: the location rule and an unreadable ZIP, without a database.</summary>
public class ZipIntegrityCheckTests
{
    private TempDir _roms = null!;
    private ISystemRegistry _systems = null!;

    [SetUp]
    public void SetUp()
    {
        _roms = new TempDir();
        _systems = Substitute.For<ISystemRegistry>();
        var gb = TestHandlers.Fake("gb", embedRomsInZip: true);
        _systems.ById("gb").Returns(gb);
    }

    [TearDown]
    public void TearDown() => _roms.Dispose();

    private ZipIntegrityCheck Check() => new(hashes: false, _systems, Options.Create(new PathsOptions { Roms = _roms.Path }),
        new InfoJsonReader([], _systems), Substitute.For<ILibraryMirror>());

    [TestCase("Game (2).zip", true)]
    [TestCase("game (10).ZIP", true)]
    [TestCase("Game (1).zip", false)]
    [TestCase("Game (02).zip", false)]
    [TestCase("Game.zip", false)]
    [TestCase("Other (2).zip", false)]
    public void IsClashSuffixed(string name, bool expected) =>
        Assert.That(ZipIntegrityCheck.IsClashSuffixed(name, "Game"), Is.EqualTo(expected));

    [TestCase("gb/G/Game.zip")]
    [TestCase("GB/g/game (2).zip")]
    public void CheckLocation_CanonicalPath_NoFinding(string zipPath)
    {
        var findings = new List<IntegrityFinding>();
        Check().CheckLocation(zipPath, "gb", "Game", Guid.NewGuid(), findings.Add);
        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void CheckLocation_WrongBucket_IsWrongLocation()
    {
        var findings = new List<IntegrityFinding>();
        var id = Guid.NewGuid();
        Check().CheckLocation("gb/X/Game.zip", "gb", "Game", id, findings.Add);
        Assert.That(findings, Is.EqualTo(new[]
        {
            new IntegrityFinding(IntegrityIssueType.WrongLocation, "gb/X/Game.zip", SystemId: "gb", GameId: id, Details: "expected gb/G/Game.zip"),
        }));
    }

    [Test]
    public async Task CheckZipAsync_MissingZip_IsZipUnreadable()
    {
        var findings = new List<IntegrityFinding>();
        await Check().CheckZipAsync("gb/G/Game.zip", null, findings.Add, CancellationToken.None);
        Assert.That(findings.Select(f => (f.Type, f.ZipPath, f.SystemId)),
            Is.EqualTo(new[] { (IntegrityIssueType.ZipUnreadable, (string?)"gb/G/Game.zip", (string?)"gb") }));
    }
}
