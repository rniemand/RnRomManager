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
        var handler = TestHandlers.Fake(id, embedRomsInZip: true);
        handler.DatName.Returns(datName);
        handler.DatFolder.Returns(DatKind.NoIntro);
        handler.MetadatKinds.Returns(DatKind.Metadat);
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
    public void ANoneHandler_GetsNoStoreAndNoError_StrictOrNot()
    {
        var none = TestHandlers.Fake("dos");
        none.DatName.Returns("DOS");
        none.DatSource.Returns(DatSource.None);
        ISystemHandler[] handlers = [Handler("gb", Gb, ".gb"), Handler("gbc", Gbc, ".gbc", ".cgb"), none];
        var registry = Substitute.For<ISystemRegistry>();
        registry.All.Returns(handlers);
        var cache = new DatCache(Options.Create(new PathsOptions { Cache = _dir.Path }), registry, NullLogger<DatCache>.Instance);
        WriteFixtures();

        cache.Load();
        var prepared = cache.Prepare(cache.Directory);

        Assert.That(cache.For("dos"), Is.Null);
        Assert.That(cache.For("gb"), Is.Not.Null);
        Assert.That(prepared.Keys, Is.EquivalentTo(new[] { "gb", "gbc" }));
    }

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
    public void RedumpSystemWithoutMetadat_LoadsWithEmptyMetadata()
    {
        // B1/B2: the main DAT under redump/, no metadat files at all.
        const string psx = "Sony - PlayStation";
        var handler = Handler("psx", psx, ".bin", ".cue", ".iso");
        handler.DatFolder.Returns(DatKind.Redump);
        handler.MetadatKinds.Returns([]);
        var registry = Substitute.For<ISystemRegistry>();
        registry.All.Returns([handler]);
        var cache = new DatCache(Options.Create(new PathsOptions { Cache = _dir.Path }), registry, NullLogger<DatCache>.Instance);
        var track = new SyntheticRomBuilder { Seed = 5 }.Build();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [psx] = [new FixtureGame("Disc Runner (USA)", "Disc Runner (USA) (Track 1).bin", track) { ReleaseYear = 1999, Genres = ["Racing"] }],
        }, layouts: new Dictionary<string, FixtureDatLayout> { [psx] = FixtureDatLayout.Redump });

        cache.Load();

        Assert.That(Directory.GetDirectories(cache.Directory).Select(Path.GetFileName), Is.EqualTo(new[] { "redump" }));
        var store = cache.For("psx")!;
        var rom = store.BySha1(Convert.ToHexStringLower(SHA1.HashData(track)))!;
        var meta = store.MetadataFor(rom.Crc32);
        Assert.That(meta.ReleaseYear, Is.Null);
        Assert.That(meta.Developers, Is.Empty);
        Assert.That(meta.Publishers, Is.Empty);
        Assert.That(meta.Genres, Is.Empty);
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

    [TestCase("link", TestName = "UnsafeManifest_LoadsNoStores linked")]
    [TestCase("fifo", TestName = "UnsafeManifest_LoadsNoStores fifo")]
    [TestCase("directory", TestName = "UnsafeManifest_LoadsNoStores directory")]
    [TestCase("oversized", TestName = "UnsafeManifest_LoadsNoStores oversized")]
    public void UnsafeManifest_LoadsNoStores(string problem)
    {
        WriteFixtures();
        var manifest = Path.Combine(_cache.Directory, DatManifest.FileName);
        var outside = Path.Combine(_dir.Path, "outside.json");
        switch (problem)
        {
            case "link":
                File.Move(manifest, outside);
                if (!TestLinks.TryLinkFile(manifest, outside))
                    Assert.Ignore("this account can't create a file link");
                break;
            case "fifo":
                File.Delete(manifest);
                if (!TestLinks.TryCreateFifo(manifest))
                    Assert.Ignore("FIFOs are a Linux test");
                break;
            case "directory":
                File.Delete(manifest);
                Directory.CreateDirectory(manifest);
                break;
            default:
                _cache.MaxManifestBytes = new FileInfo(manifest).Length - 1;
                break;
        }

        Assert.DoesNotThrow(_cache.Load);

        Assert.That(_cache.For("gb"), Is.Null);
        Assert.That(_cache.For("gbc"), Is.Null);
    }

    [Test]
    public void ManifestAtTheCap_Loads()
    {
        WriteFixtures();
        _cache.MaxManifestBytes = new FileInfo(Path.Combine(_cache.Directory, DatManifest.FileName)).Length;

        _cache.Load();

        Assert.That(_cache.For("gb"), Is.Not.Null);
    }

    // Arcade D5: a Mame system's store, and the manifest checks that disable only that system.
    private DatCache MameCache(params ISystemHandler[] extra)
    {
        var mame = Handler("mame2003", DatStore.MameName, ".mamez");
        mame.DatSource.Returns(DatSource.Mame);
        var registry = Substitute.For<ISystemRegistry>();
        ISystemHandler[] handlers = [Handler("gb", Gb, ".gb"), mame, .. extra];
        registry.All.Returns(handlers);
        var cache = new DatCache(Options.Create(new PathsOptions { Cache = _dir.Path }), registry, NullLogger<DatCache>.Instance);
        FixtureDatWriter.Write(cache.Directory,
            new Dictionary<string, IReadOnlyList<FixtureGame>> { [Gb] = [new FixtureGame("Tetris (World) (Rev 1)", "Tetris (World) (Rev 1).gb", _tetris)] },
            mame: new Dictionary<string, string>(IngestSeeder.MinimalMame) { [FbneoDat] = IngestSeeder.MinimalMame["MAME"] });
        return cache;
    }

    private const string FbneoDat = "FBNeo - Arcade Games";

    // FBNeo D5: an Fbneo system loads its own mame/<DatName>.xml under its DatName and first ROM extension.
    [Test]
    public void Fbneo_LoadsItsFileUnderItsOwnDatName()
    {
        var fbneo = Handler("fbneo", FbneoDat, ".fbneoz", ".zip");
        fbneo.DatSource.Returns(DatSource.Fbneo);
        var cache = MameCache(fbneo);

        cache.Load();

        var store = cache.For("fbneo")!;
        Assert.That((store.DatName, store.Version), Is.EqualTo((FbneoDat, FixtureDatWriter.MameVersion)));
        Assert.That(store.Roms[0].RomName, Is.EqualTo("rnmin.fbneoz"));
        Assert.That(cache.For("mame2003")!.Roms[0].RomName, Is.EqualTo("rnmin.mamez"));
    }

    [Test]
    public void Romset_WithNoRomExtensions_FailsOnlyThatSystem()
    {
        var fbneo = Handler("fbneo", FbneoDat);
        fbneo.DatSource.Returns(DatSource.Fbneo);
        var cache = MameCache(fbneo);

        cache.Load();

        Assert.That(cache.For("fbneo"), Is.Null);
        Assert.That(cache.For("mame2003"), Is.Not.Null);
    }

    private void EditMameEntry(DatCache cache, Func<DatManifestEntry, DatManifestEntry> change)
    {
        var path = Path.Combine(cache.Directory, DatManifest.FileName);
        DatManifest.Write(path, [.. DatManifest.Read(path).Select(e => e.File == MameDat.RelativePath("MAME") ? change(e) : e)]);
    }

    [Test]
    public void Mame_LoadsTheStore()
    {
        var cache = MameCache();

        cache.Load();

        var store = cache.For("mame2003")!;
        Assert.That((store.DatName, store.Version), Is.EqualTo(("MAME", FixtureDatWriter.MameVersion)));
        Assert.That(store.BySetName("RNMIN")!.Description, Is.EqualTo("Rn Minimal (World)"));
    }

    private static readonly object[] BadMame =
    [
        new object[] { "missing file", (Action<DatCache>)(c => File.Delete(Path.Combine(c.Directory, "mame", "MAME.xml"))) },
        new object[] { "SHA1 mismatch", (Action<DatCache>)(c => File.AppendAllText(Path.Combine(c.Directory, "mame", "MAME.xml"), " ")) },
    ];

    [TestCaseSource(nameof(BadMame))]
    public void Mame_BadFile_DisablesOnlyMame(string name, Action<DatCache> damage)
    {
        var cache = MameCache();
        damage(cache);

        cache.Load();

        Assert.That(cache.For("mame2003"), Is.Null, name);
        Assert.That(cache.For("gb"), Is.Not.Null);
        Assert.Throws<InvalidDataException>(() => cache.Prepare(cache.Directory));
    }

    [TestCase("wrong DatName")]
    [TestCase("null version")]
    [TestCase("not listed")]
    public void Mame_BadManifestRow_DisablesOnlyMame(string kind)
    {
        var cache = MameCache();
        var path = Path.Combine(cache.Directory, DatManifest.FileName);
        if (kind == "not listed")
            DatManifest.Write(path, [.. DatManifest.Read(path).Where(e => !e.File.StartsWith("mame/"))]);
        else
            EditMameEntry(cache, e => kind == "wrong DatName" ? e with { DatName = "MAME2" } : e with { Version = null });

        cache.Load();

        Assert.That(cache.For("mame2003"), Is.Null);
        Assert.That(cache.For("gb"), Is.Not.Null);
        var ex = Assert.Throws<InvalidDataException>(() => cache.Prepare(cache.Directory));
        Assert.That(ex!.Message, Does.Contain("mame/MAME.xml"));
    }
}
