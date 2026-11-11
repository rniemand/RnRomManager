using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Spec E4/E2: ZIP paths compared by MariaDB with the collation of <c>games.zip_path</c> (case-insensitive, accent-sensitive).</summary>
[Category("Integration")]
public class ZipPathEquivalenceTests
{
    // "é" as one code point, and as "e" + a combining acute: equal to the database, different to an ordinal compare.
    private const string Composed = "ext/C/Café.zip";
    private const string Decomposed = "ext/C/Café.zip";
    private const string Plain = "ext/C/Cafe.zip";

    private ILibraryMirror _mirror = null!;
    private GameRepository _games = null!;
    private RomRepository _roms = null!;
    private TempDir _dir = null!;
    private int _seed;

    [SetUp]
    public async Task SetUp()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _mirror = new LibraryMirror(factory);
        _games = new GameRepository(factory);
        _roms = new RomRepository(factory);
        _dir = new TempDir();
        _seed = 0;
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private async Task<Guid> MirrorAsync(string zipPath)
    {
        var seed = ++_seed;
        var game = Game("Game " + seed, Rom($"Game {seed}.nsp", RomBytes(seed)) with { ExternalFileName = $"Game {seed}.Game {seed}.nsp" }) with { System = "ext" };
        await _mirror.ReplaceAsync(new MirrorEntry(game, zipPath, 1, DateTime.UtcNow), CancellationToken.None);
        return game.GameId;
    }

    private Task<string> AllocateAsync(string title, Guid? ownGameId = null, string? ownZipPath = null) =>
        AllocateWithClaimsAsync(title, ownGameId, ownZipPath);

    private async Task<string> AllocateWithClaimsAsync(string title, Guid? ownGameId, string? ownZipPath) =>
        await ExternalRomName.AllocateZipPathAsync(_dir.Path, "ext", title, await _roms.ListExternalAsync(CancellationToken.None), _games,
            CancellationToken.None, ownGameId, ownZipPath);

    [Test]
    public async Task ComposedAndDecomposedAccents_AreOneGroup_ThePlainLetterIsSeparate()
    {
        Assert.That(string.Equals(Composed, Decomposed, StringComparison.OrdinalIgnoreCase), Is.False, "an ordinal-ignore-case check can't see this clash");

        var groups = await _games.ListZipPathEquivalenceAsync([Composed, Plain, Decomposed], CancellationToken.None);

        Assert.That(groups.Select(g => g.Paths), Is.EqualTo(new[] { new[] { Composed, Decomposed }, new[] { Plain } }));
        Assert.That(groups.SelectMany(g => g.Existing), Is.Empty);
    }

    [Test]
    public async Task ExistingRows_AreMatchedByTheDatabase_WithTheirStoredSpelling()
    {
        var cafe = await MirrorAsync(Decomposed);
        var other = await MirrorAsync("ext/O/Orbit.zip");

        var groups = await _games.ListZipPathEquivalenceAsync([Plain, Composed, "EXT/o/ORBIT.ZIP", "ext/N/None.zip"], CancellationToken.None);

        Assert.That(groups.Select(g => (string.Join("|", g.Paths), string.Join("|", g.Existing.Select(e => $"{e.GameId} {e.ZipPath}")))), Is.EqualTo(new[]
        {
            (Plain, ""),
            (Composed, $"{cafe} {Decomposed}"),
            ("EXT/o/ORBIT.ZIP", $"{other} ext/O/Orbit.zip"),
            ("ext/N/None.zip", ""),
        }));
    }

    [Test]
    public async Task EquivalentPair_SplitAcrossInputBatches_StaysOneGroup()
    {
        // 1,201 inputs are three INSERTs of 500, 500 and 201 rows; the pair sits in the first and the last.
        var paths = Enumerable.Range(0, 1201).Select(i => $"ext/F/Filler {i:D4}.zip").ToList();
        paths[3] = Composed;
        paths[1200] = Decomposed;

        var groups = await _games.ListZipPathEquivalenceAsync(paths, CancellationToken.None);

        Assert.That(groups, Has.Count.EqualTo(1200));
        Assert.That(groups.Single(g => g.Paths.Count > 1).Paths, Is.EqualTo(new[] { Composed, Decomposed }));
        Assert.That(groups.SelectMany(g => g.Paths), Is.EquivalentTo(paths), "every spelling comes back unchanged");
    }

    [Test]
    public async Task EmptyInput_RepeatedCalls_AndOverlongPaths_AreHandled()
    {
        var overlong = "ext/L/" + new string('x', 600) + ".zip";

        Assert.That(await _games.ListZipPathEquivalenceAsync([], CancellationToken.None), Is.Empty);
        // Twice on a pooled connection: the staging table of the first call must be gone.
        Assert.That(await _games.ListZipPathEquivalenceAsync([Composed], CancellationToken.None), Has.Count.EqualTo(1));
        var groups = await _games.ListZipPathEquivalenceAsync([overlong, Composed, overlong], CancellationToken.None);

        Assert.That(groups.Select(g => g.Paths.Count), Is.EqualTo(new[] { 1, 1, 1 }), "a path longer than the column can't equal a stored one");
    }

    // --- external ZIP path allocation ---

    [Test]
    public async Task Allocate_FreeTitle_IsTheBucketedPath()
    {
        Assert.That(await AllocateAsync("Café"), Is.EqualTo(Composed));
    }

    [Test]
    public async Task Allocate_DbOnlyClaimOnAnEquivalentPath_ForcesASuffix()
    {
        await MirrorAsync(Decomposed); // a row only: no ZIP on disk

        Assert.That(await AllocateAsync("Café"), Is.EqualTo("ext/C/Café (2).zip"));
    }

    [Test]
    public async Task Allocate_EquivalentUnmirroredZipOnDisk_ForcesASuffix()
    {
        Directory.CreateDirectory(_dir.Combine("ext", "C"));
        File.WriteAllText(_dir.Combine("ext", "C", "Café.zip"), "a ZIP no sync has mirrored yet");

        Assert.That(await AllocateAsync("Café"), Is.EqualTo("ext/C/Café (2).zip"));
        Assert.That(await AllocateAsync("Cafe"), Is.EqualTo(Plain), "the unaccented name is a different path");
    }

    [Test]
    public async Task Allocate_OwnRowAndOwnZip_AreNotAClash()
    {
        var own = await MirrorAsync(Decomposed);
        Directory.CreateDirectory(_dir.Combine("ext", "C"));
        File.WriteAllText(_dir.Combine("ext", "C", "Café.zip"), "this game's ZIP");

        Assert.That(await AllocateAsync("Café", own, Decomposed), Is.EqualTo(Composed));
        Assert.That(await AllocateAsync("Café"), Is.EqualTo("ext/C/Café (2).zip"), "for any other game it is taken");
    }
}
