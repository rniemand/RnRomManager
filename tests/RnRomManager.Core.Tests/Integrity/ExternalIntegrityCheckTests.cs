using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Integrity;

/// <summary>G1/G4: external file keys, shared claims and the stat checks of a Quick run.</summary>
public class ExternalIntegrityCheckTests
{
    private const string Zip = "ps/G/Game.zip";
    private TempDir _roms = null!;
    private ISystemRegistry _systems = null!;

    [SetUp]
    public void SetUp()
    {
        _roms = new TempDir();
        Directory.CreateDirectory(_roms.Combine("ps", "G"));
        _systems = Substitute.For<ISystemRegistry>();
        var ps = TestHandlers.Fake("ps");
        _systems.ById("ps").Returns(ps);
    }

    [TearDown]
    public void TearDown() => _roms.Dispose();

    private ExternalIntegrityCheck Check() => new(hashes: false, hooks: null, _systems, Options.Create(new PathsOptions { Roms = _roms.Path }),
        TimeProvider.System, NullLogger.Instance);

    private static ExternalRomRow Row(
        Guid game,
        string zip,
        string file,
        long size = 5) => new(game, zip, file, size, "sha1");

    private static ExternalChecks Checks(params ExternalRomRow[] rows) =>
        new(rows.ToLookup(r => r.ZipPath, StringComparer.OrdinalIgnoreCase), ExternalIntegrityCheck.Claimants(rows), [], []);

    private async Task<List<IntegrityFinding>> Run(ExternalChecks checks)
    {
        var findings = new List<IntegrityFinding>();
        await Check().CheckExternalAsync(Zip, checks.ByZip[Zip], checks, findings.Add, new JobProgress(), CancellationToken.None);
        return findings;
    }

    [Test]
    public void FileKey_IsTheZipFolderPlusTheFileName() =>
        Assert.That(ExternalIntegrityCheck.FileKey(Row(Guid.NewGuid(), Zip, "Game.bin")), Is.EqualTo("ps/G/Game.bin"));

    [Test]
    public void Claimants_OnlyFilesTwoGamesClaim_IgnoringCase()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var claimants = ExternalIntegrityCheck.Claimants(
            [Row(a, "ps/G/A.zip", "Shared.bin"), Row(b, "ps/G/B.zip", "shared.BIN"), Row(Guid.NewGuid(), "ps/G/C.zip", "Own.bin")]);
        Assert.That(claimants.Keys, Is.EqualTo(new[] { "ps/G/Shared.bin" }));
        Assert.That(claimants["PS/g/shared.bin"], Is.EqualTo(new[] { a, b }));
    }

    [Test]
    public async Task MissingFile_IsEntryMissing_AndChecked()
    {
        var checks = Checks(Row(Guid.NewGuid(), Zip, "Game.bin"));
        var findings = await Run(checks);
        Assert.That(findings.Select(f => (f.Type, f.EntryName)), Is.EqualTo(new[] { (IntegrityIssueType.EntryMissing, (string?)"Game.bin") }));
        Assert.That(checks.Outcome(Zip, "Game.bin"), Is.False);
    }

    [Test]
    public async Task WrongSize_IsEntrySizeMismatch_WithoutHashing()
    {
        File.WriteAllBytes(_roms.Combine("ps", "G", "Game.bin"), [1, 2, 3]);
        var checks = Checks(Row(Guid.NewGuid(), Zip, "Game.bin", size: 5));
        var findings = await Run(checks);
        Assert.That(findings.Select(f => (f.Type, f.Details)),
            Is.EqualTo(new[] { (IntegrityIssueType.EntrySizeMismatch, (string?)"file 3 bytes, info.json 5") }));
        Assert.That(checks.Outcome(Zip, "Game.bin"), Is.False);
    }

    [Test]
    public async Task SharedFile_IsEntryMissingForEachOwner_NotRead()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        File.WriteAllBytes(_roms.Combine("ps", "G", "Game.bin"), [1, 2, 3, 4, 5]);
        var findings = await Run(Checks(Row(a, Zip, "Game.bin"), Row(b, "ps/G/Other.zip", "Game.bin")));
        Assert.That(findings.Select(f => (f.Type, f.GameId, f.Details)),
            Is.EqualTo(new[] { (IntegrityIssueType.EntryMissing, (Guid?)a, (string?)$"also claimed by game {b}") }));
    }
}
