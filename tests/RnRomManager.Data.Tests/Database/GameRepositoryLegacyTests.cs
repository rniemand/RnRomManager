using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

/// <summary>S4, ROM Zip Config B7: <c>ListStorageMovesAsync</c> selects candidates of the given systems whose layout differs from their storage.</summary>
[Category("Integration")]
public class GameRepositoryLegacyTests
{
    private GameRepository _games = null!;
    private ILibraryMirror _mirror = null!;
    private int _seed;

    [SetUp]
    public async Task SetUp()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _games = new GameRepository(factory);
        _mirror = new LibraryMirror(factory);
    }

    private async Task<Guid> Mirror(string system, string zipPath, params string?[] pointers)
    {
        var roms = pointers.Select((p, i) => TestLibrary.Rom($"R{i}.nds", TestLibrary.RomBytes(++_seed), isPrimary: i == 0)
            with { ExternalFileName = p }).ToArray();
        var game = TestLibrary.Game(Path.GetFileNameWithoutExtension(zipPath), roms) with { System = system };
        await _mirror.ReplaceAsync(new MirrorEntry(game, zipPath, 100, DateTime.UtcNow), default);
        return game.GameId;
    }

    [Test]
    public async Task ExternalSystems_ListGamesWithARomInsideTheZip_ByZipPath()
    {
        var zeta = await Mirror("nds", "nds/Z/Zeta.zip", (string?)null);
        var alpha = await Mirror("nds", "nds/A/Alpha.zip", null, null);
        var mixed = await Mirror("nds", "nds/M/Mixed.zip", "Mixed.R0.nds", null);
        await Mirror("nds", "nds/E/External.zip", "External.R0.nds");
        await Mirror("gb", "gb/T/Tetris.zip", (string?)null);

        var listed = await _games.ListStorageMovesAsync([], ["nds"], default);

        Assert.That(listed.Select(g => g.Id), Is.EqualTo(new[] { alpha, mixed, zeta }));
        Assert.That(await _games.ListStorageMovesAsync([], [], default), Is.Empty);
    }

    [Test]
    public async Task EmbedSystems_ListGamesWithARomBesideTheZip_BothDirectionsTogether()
    {
        await Mirror("nds", "nds/Z/Zeta.zip", (string?)null);
        var mixed = await Mirror("nds", "nds/M/Mixed.zip", "Mixed.R0.nds", null);
        var external = await Mirror("nds", "nds/E/External.zip", "External.R0.nds");
        var legacy = await Mirror("legacy", "legacy/L/Legacy.zip", (string?)null);
        await Mirror("legacy", "legacy/X/Ext.zip", "Ext.R0.nds");
        await Mirror("ext", "ext/O/Other.zip", "Other.R0.nds");

        var listed = await _games.ListStorageMovesAsync(["nds"], ["legacy"], default);

        Assert.That(listed.Select(g => g.Id), Is.EqualTo(new[] { legacy, external, mixed }), "only the given systems, by ZIP path");
    }
}
