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

    [TestCase(SeedSet.Basic, 41)]
    [TestCase(SeedSet.Full, 241)]
    public void Sets_HaveExpectedFilesArchivesAndReadyAges(SeedSet set, int count)
    {
        var ingest = _dir.Combine("ingest");
        IngestSeeder.Seed(ingest, _dir.Combine("cache"), set, _time);
        var files = Directory.GetFiles(ingest, "*", SearchOption.AllDirectories);
        Assert.That(files, Has.Length.EqualTo(count));
        Assert.That(files.Count(f => Path.GetExtension(f) == ".zip"), Is.EqualTo(1));
        Assert.That(files.Count(f => Path.GetExtension(f) == ".7z"), Is.EqualTo(1));
        // Switch: a base, its update and a DLC, each a PFS0 of at least 1 KiB naming its ticket, and a ticketless [UPD] update.
        var switchFiles = Directory.GetFiles(Path.Combine(ingest, "switch")).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.That(switchFiles, Is.EqualTo(new[]
        {
            "01-orbit-expedition.nsp", "02-orbit-expedition [v65536].nsp", "03-orbit-expedition-bonus.nsp", "04-orbit-expedition [0100000000010000][UPD][1.1.0].nsp",
        }));
        var ticketless = File.ReadAllBytes(Path.Combine(ingest, "switch", "04-orbit-expedition [0100000000010000][UPD][1.1.0].nsp"));
        Assert.That((ticketless[..4], System.Text.Encoding.ASCII.GetString(ticketless).Contains(".tik")), Is.EqualTo(("PFS0"u8.ToArray(), false)));
        foreach (var (name, id) in new[] { ("01-orbit-expedition.nsp", "0100000000010000"), ("02-orbit-expedition [v65536].nsp", "0100000000010800"), ("03-orbit-expedition-bonus.nsp", "0100000000011001") })
        {
            var bytes = File.ReadAllBytes(Path.Combine(ingest, "switch", name));
            Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(1024), name);
            Assert.That(bytes[..4], Is.EqualTo("PFS0"u8.ToArray()), name);
            Assert.That(System.Text.Encoding.ASCII.GetString(bytes), Does.Contain(id + "0000000000000000.tik"), name);
        }
        Assert.That(files.All(f => File.GetLastWriteTimeUtc(f) == _time.GetUtcNow().AddMinutes(-1).UtcDateTime), Is.True);
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "gb", "01-orbit-world.gb")), Is.EqualTo(File.ReadAllBytes(Path.Combine(ingest, "gb", "90-duplicate.gb"))));
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "gb", "04-color-garden.gbc"))[SyntheticRomBuilder.CgbFlagOffset], Is.EqualTo(0x80));
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "n64", "01-orbit-kart.v64"))[1], Is.EqualTo(0x80), "byte-swapped");
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "nes", "01-pixel-pioneer.nes"))[..4], Is.EqualTo("NES"u8.ToArray()));
        Assert.That(new FileInfo(Path.Combine(ingest, "snes", "01-mode-seven-rally.smc")).Length % 1024, Is.EqualTo(512), "copier-headered");
        Assert.That(new FileInfo(Path.Combine(ingest, "a2600", "01-laser-lanes.bin")).Length, Is.EqualTo(4096));
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "coleco", "01-pixel-pursuit.rom"))[..2], Is.EqualTo(new byte[] { 0xAA, 0x55 }));
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "gg", "01-pocket-drift.gg"))[0x7FF0..0x7FF8], Is.EqualTo("TMR SEGA"u8.ToArray()));
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "sms", "01-harbor-patrol.sms"))[0x7FF0..0x7FF8], Is.EqualTo("TMR SEGA"u8.ToArray()));
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "md", "01-turbo-comet.gen"))[0x100..0x104], Is.EqualTo("SEGA"u8.ToArray()));
        Assert.That(File.ReadAllBytes(Path.Combine(ingest, "s32x", "01-star-relay.32x"))[0x100..0x108], Is.EqualTo("SEGA 32X"u8.ToArray()));
        // Disc systems: a bin/cue set for PSX and Saturn, one .iso for the rest; every image 64 KiB and unique.
        string[] Names(string system) => [.. Directory.GetFiles(Path.Combine(ingest, system)).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
        Assert.That(Names("psx"), Is.EqualTo(new[] { "Disc Runner (USA) (Track 1).bin", "Disc Runner (USA) (Track 2).bin", "Disc Runner (USA).cue" }));
        Assert.That(Names("saturn"), Is.EqualTo(new[] { "Sky Rally (Japan) (Track 1).bin", "Sky Rally (Japan) (Track 2).bin", "Sky Rally (Japan).cue" }));
        Assert.That((Names("psp"), Names("gc"), Names("wii"), Names("x360")), Is.EqualTo((new[] { "Pocket Ninja (USA).iso" }, new[] { "Cube Kart (USA).iso" },
            new[] { "Wave Bowling (USA).iso" }, new[] { "Arc Strike (USA).iso" })));
        var images = new[] { "psx", "saturn", "psp", "gc", "wii", "x360" }.SelectMany(s => Directory.GetFiles(Path.Combine(ingest, s)))
            .Where(f => Path.GetExtension(f) != ".cue").ToList();
        Assert.That(images.Select(f => new FileInfo(f).Length), Is.All.EqualTo(64 * 1024));
        Assert.That(images.Select(f => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(f)))).Distinct().Count(), Is.EqualTo(8));
        Assert.That(File.ReadAllText(Path.Combine(ingest, "psx", "Disc Runner (USA).cue")), Does.Contain("FILE \"Disc Runner (USA) (Track 2).bin\" BINARY"));
        Assert.That(new FileInfo(Path.Combine(ingest, "tg16", "01-neon-circuit.pce")).Length, Is.EqualTo(262656), "copier-headered");
        Assert.That(Directory.GetFiles(_dir.Combine("cache", "dats"), "*.dat", SearchOption.AllDirectories), Has.Length.EqualTo(96));
        Assert.That(File.Exists(_dir.Combine("cache", "dats", "redump", IngestSeeder.WiiDat + ".dat")), Is.True, "the disc systems' DATs go in redump/");
        Assert.That(File.Exists(_dir.Combine("cache", "dats", "titledb", "Nintendo - Nintendo Switch.json")), Is.True);
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
