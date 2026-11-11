using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Dats;

/// <summary>Loading <c>cache/dats/</c> written by <see cref="FixtureDatWriter"/> (spec §10.1, §15.2).</summary>
public class DatCacheTests
{
    private const string Gb = "Nintendo - Game Boy";
    private const string Gbc = "Nintendo - Game Boy Color";

    private TempDir _dir = null!;
    private DatCache _cache = null!;
    private byte[] _tetris = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = new TempDir();
        ISystemHandler[] handlers = [Handler("gb", Gb, ".gb"), Handler("gbc", Gbc, ".gbc", ".cgb")];
        var registry = Substitute.For<ISystemRegistry>();
        registry.All.Returns(handlers);
        _cache = new DatCache(Options.Create(new PathsOptions { Cache = _dir.Path }), registry, NullLogger<DatCache>.Instance);
        _tetris = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private static ISystemHandler Handler(string id, string datName, params string[] extensions)
    {
        var handler = Substitute.For<ISystemHandler>();
        handler.EmbedRomsInZip.Returns(true);
        handler.Id.Returns(id);
        handler.DatName.Returns(datName);
        handler.RomExtensions.Returns(extensions);
        return handler;
    }

    private void WriteFixtures() => FixtureDatWriter.Write(_cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
    {
        [Gb] =
        [
            new FixtureGame("Tetris (World) (Rev 1)", "Tetris (World) (Rev 1).gb", _tetris)
            {
                ReleaseYear = 1989,
                Developers = ["Nintendo", "Bullet-Proof Software"],
                Publishers = ["Nintendo"],
                Genres = ["Puzzle"],
            },
        ],
        [Gbc] = [new FixtureGame("Colour Game (USA)", "Colour Game (USA).gbc", new SyntheticRomBuilder { Seed = 2, CgbFlag = 0xC0 }.Build())],
    });

    [Test]
    public void WriterThenLoader_RoundTripsHashesVersionAndMetadat()
    {
        WriteFixtures();

        _cache.Load();

        var gb = _cache.For("gb")!;
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(_tetris));
        var rom = gb.BySha1(sha1)!;
        Assert.That((gb.DatName, gb.Version), Is.EqualTo((Gb, FixtureDatWriter.Version)));
        Assert.That((rom.RomName, rom.SizeBytes, rom.Md5), Is.EqualTo(("Tetris (World) (Rev 1).gb", (long)_tetris.Length, Convert.ToHexStringLower(MD5.HashData(_tetris)))));
        var meta = gb.MetadataFor(rom.Crc32);
        Assert.That(meta.ReleaseYear, Is.EqualTo(1989));
        Assert.That(meta.Developers, Is.EqualTo(new[] { "Nintendo", "Bullet-Proof Software" }));
        Assert.That(_cache.For("gbc")!.Roms, Has.Count.EqualTo(1));
    }

    [Test]
    public void DatRomsWithAnExtensionTheSystemDoesNotAccept_AreLeftOut()
    {
        // N3: like N64's .bin entries next to its .z64 dumps.
        var bin = new SyntheticRomBuilder { Seed = 3 }.Build();
        FixtureDatWriter.Write(_cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [Gb] = [new FixtureGame("Tetris (World) (Rev 1)", "Tetris (World) (Rev 1).gb", _tetris)],
            [Gbc] =
            [
                new FixtureGame("Colour Game (USA)", "Colour Game (USA).gbc", new SyntheticRomBuilder { Seed = 2, CgbFlag = 0xC0 }.Build()),
                new FixtureGame("Colour Tool (USA)", "Colour Tool (USA).bin", bin),
            ],
        });

        _cache.Load();

        var gbc = _cache.For("gbc")!;
        Assert.That(gbc.Roms.Select(r => r.RomName), Is.EqualTo(new[] { "Colour Game (USA).gbc" }));
        Assert.That(gbc.BySha1(Convert.ToHexStringLower(SHA1.HashData(bin))), Is.Null);
        Assert.That(gbc.BaseTitles, Is.EqualTo(new[] { "Colour Game" }));
    }

    [Test]
    public void Manifest_HasFiveFilesPerSystem_MetadatWithoutVersion()
    {
        WriteFixtures();

        var manifest = DatManifest.Read(Path.Combine(_cache.Directory, DatManifest.FileName));

        Assert.That(manifest, Has.Count.EqualTo(10));
        Assert.That(manifest.Single(e => e.File == $"no-intro/{Gb}.dat").Version, Is.EqualTo(FixtureDatWriter.Version));
        Assert.That(manifest.Where(e => !e.File.StartsWith("no-intro/", StringComparison.Ordinal)).Select(e => e.Version), Has.All.Null);
    }

    [Test]
    public void Sha1Mismatch_MakesOnlyThatSystemCorrupt()
    {
        WriteFixtures();
        File.AppendAllText(Path.Combine(_cache.Directory, "genre", $"{Gb}.dat"), "\n");

        _cache.Load();

        Assert.That(_cache.For("gb"), Is.Null);
        Assert.That(_cache.For("gbc"), Is.Not.Null);
    }

    [Test]
    public void MissingFile_MakesTheSystemCorrupt()
    {
        WriteFixtures();
        File.Delete(Path.Combine(_cache.Directory, "publisher", $"{Gbc}.dat"));

        _cache.Load();

        Assert.That((_cache.For("gb") is null, _cache.For("gbc") is null), Is.EqualTo((false, true)));
    }

    [Test]
    public void UnparsableFileWithMatchingSha1_IsCorrupt()
    {
        WriteFixtures();
        var path = Path.Combine(_cache.Directory, "no-intro", $"{Gb}.dat");
        File.WriteAllText(path, "clrmamepro ( name \"broken\"");
        RewriteManifestSha1($"no-intro/{Gb}.dat", path);

        _cache.Load();

        Assert.That(_cache.For("gb"), Is.Null);
    }

    [Test]
    public void NoIntroForAnotherSystem_IsCorrupt()
    {
        WriteFixtures();
        var gbPath = Path.Combine(_cache.Directory, "no-intro", $"{Gb}.dat");
        File.Copy(Path.Combine(_cache.Directory, "no-intro", $"{Gbc}.dat"), gbPath, overwrite: true);
        RewriteManifestSha1($"no-intro/{Gb}.dat", gbPath);

        _cache.Load();

        Assert.That(_cache.For("gb"), Is.Null);
    }

    [TestCase(null)]
    [TestCase("not json")]
    public void MissingOrCorruptManifest_NoSystemHasDats(string? manifest)
    {
        WriteFixtures();
        var path = Path.Combine(_cache.Directory, DatManifest.FileName);
        if (manifest is null)
            File.Delete(path);
        else
            File.WriteAllText(path, manifest);

        _cache.Load();

        Assert.That((_cache.For("gb"), _cache.For("gbc")), Is.EqualTo(((DatStore?)null, (DatStore?)null)));
    }

    [Test]
    public void FileNotListedInTheManifest_IsCorrupt()
    {
        WriteFixtures();
        var path = Path.Combine(_cache.Directory, DatManifest.FileName);
        // Files are looked up by the fixed layout, never by a path from the manifest, so "../x" can't be named.
        DatManifest.Write(path, DatManifest.Read(path).Select(e => e.File == $"genre/{Gb}.dat" ? e with { File = "../genre.dat" } : e));

        _cache.Load();

        Assert.That(_cache.For("gb"), Is.Null);
    }

    [Test]
    public void Reload_SwapsTheWholeSet()
    {
        WriteFixtures();
        _cache.Load();
        var before = _cache.For("gb");

        File.Delete(Path.Combine(_cache.Directory, DatManifest.FileName));
        _cache.Load();

        Assert.That((before is null, _cache.For("gb") is null), Is.EqualTo((false, true)));
    }

    private void RewriteManifestSha1(string file, string path)
    {
        var manifestPath = Path.Combine(_cache.Directory, DatManifest.FileName);
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(path)));
        DatManifest.Write(manifestPath, DatManifest.Read(manifestPath).Select(e => e.File == file ? e with { Sha1 = sha1 } : e));
    }
}
