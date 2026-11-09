using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Ingest;

public class IngestSeederTests
{
    private TempDir _dir = null!;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
    [SetUp] public void SetUp() => _dir = new TempDir();
    [TearDown] public void TearDown() => _dir.Dispose();

    [TestCase(SeedSet.Basic, 16)]
    [TestCase(SeedSet.Full, 216)]
    public void Sets_HaveExpectedFilesArchivesAndReadyAges(SeedSet set, int count)
    {
        var ingest = _dir.Combine("ingest");
        IngestSeeder.Seed(ingest, _dir.Combine("cache"), set, _time);
        var files = Directory.GetFiles(ingest, "*", SearchOption.AllDirectories);
        Assert.That(files, Has.Length.EqualTo(count));
        Assert.That(files.Count(f => Path.GetExtension(f) == ".zip"), Is.EqualTo(1));
        Assert.That(files.Count(f => Path.GetExtension(f) == ".7z"), Is.EqualTo(1));
        Assert.That(files.All(f => File.GetLastWriteTimeUtc(f) == _time.GetUtcNow().AddMinutes(-1).UtcDateTime), Is.True);
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "gb", "01-orbit-world.gb")), Is.EqualTo(File.ReadAllBytes(Path.Combine(ingest, "gb", "90-duplicate.gb"))));
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "gb", "04-color-garden.gbc"))[SyntheticRomBuilder.CgbFlagOffset], Is.EqualTo(0x80));
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "n64", "01-orbit-kart.v64"))[1], Is.EqualTo(0x80), "byte-swapped");
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "nes", "01-pixel-pioneer.nes"))[..4], Is.EqualTo("NES"u8.ToArray()));
        Assert.That(new FileInfo(Path.Combine(ingest, "snes", "01-mode-seven-rally.smc")).Length % 1024, Is.EqualTo(512), "copier-headered");
        Assert.That(new FileInfo(Path.Combine(ingest, "a2600", "01-laser-lanes.bin")).Length, Is.EqualTo(4096));
        Assert.That(Directory.GetFiles(_dir.Combine("cache", "dats"), "*.dat", SearchOption.AllDirectories), Has.Length.EqualTo(40));
    }

    [Test]
    public void RepeatedSeeds_AreByteIdentical_AndPreserveUnrelatedFiles()
    {
        var ingest = _dir.Combine("ingest");
        var cache = _dir.Combine("cache");
        IngestSeeder.Seed(ingest, cache, SeedSet.Basic, _time);
        var before = Directory.GetFiles(_dir.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        File.WriteAllText(Path.Combine(ingest, "keep.txt"), "keep");
        IngestSeeder.Seed(ingest, cache, SeedSet.Basic, _time);
        foreach (var (path, hash) in before) Assert.That(SHA256.HashData(File.ReadAllBytes(path)), Is.EqualTo(hash), path);
        Assert.That(File.ReadAllText(Path.Combine(ingest, "keep.txt")), Is.EqualTo("keep"));
    }

    [TestCase("same", "same")]
    [TestCase("root", "root/cache")]
    [TestCase("root/ingest", "root")]
    public void OverlappingPaths_AreRejectedBeforeWriting(string ingest, string cache)
    {
        Assert.Throws<ArgumentException>(() => IngestSeeder.Seed(_dir.Combine(ingest), _dir.Combine(cache), SeedSet.Basic, _time));
        Assert.That(Directory.GetFileSystemEntries(_dir.Path), Is.Empty);
    }

    [TestCase("")]
    [TestCase("--set Basic")]
    [TestCase("--ingest x --cache y --set Other")]
    [TestCase("--ingest x --cache y --set 0")]
    [TestCase("--ingest x --cache y --set Basic --set Full")]
    [TestCase("--unknown x")]
    [TestCase("--ingest --cache y --set Basic")]
    public void Cli_InvalidArguments_ReturnOneAndUsage(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.That(TestDataCommand.Run(command.Split(' ', StringSplitOptions.RemoveEmptyEntries), output, error, _time), Is.EqualTo(1));
        Assert.That(error.ToString(), Does.Contain("Usage:"));
    }

    [Test]
    public void Cli_ValidArgumentsIncludingSpaces_SeedBothRoots()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.That(TestDataCommand.Run(["--ingest", _dir.Combine("ingest space"), "--cache", _dir.Combine("cache space"), "--set", "Basic"], output, error, _time), Is.Zero);
        Assert.That(output.ToString(), Does.Contain("Seeded Basic"));
        Assert.That(error.ToString(), Is.Empty);
        Assert.That(File.Exists(_dir.Combine("cache space", "dats", "dats.json")), Is.True);
    }

    [Test]
    public void Cli_IoFailure_ReturnsOneWithReason()
    {
        var path = _dir.Combine("file");
        File.WriteAllText(path, "not a directory");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.That(TestDataCommand.Run(["--ingest", path, "--cache", _dir.Combine("cache"), "--set", "Basic"], output, error, _time), Is.EqualTo(1));
        Assert.That(error.ToString(), Does.Contain("Seeding failed:"));
    }
}
