using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Core.Library;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

/// <summary>SG-1000 S10: <c>ListByOriginalExtensionAsync</c> lists a system's games whose every release was dropped with the extension.</summary>
[Category("Integration")]
public class GameRepositoryOriginalExtensionTests
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

    private async Task<Guid> Mirror(
        string system,
        string zipPath,
        IdentificationMethod method,
        params string[] originals)
    {
        var roms = originals.Select((o, i) => TestLibrary.Rom($"R{i}.sms", TestLibrary.RomBytes(++_seed), isPrimary: i == 0)
            with { OriginalFileName = o, Identification = new InfoJsonIdentification(method) }).ToArray();
        var game = TestLibrary.Game(Path.GetFileNameWithoutExtension(zipPath), roms) with { System = system };
        await _mirror.ReplaceAsync(new MirrorEntry(game, zipPath, 100, DateTime.UtcNow), default);
        return game.GameId;
    }

    [Test]
    public async Task ListsOnlyGamesWhoseEveryOriginalHasTheExtension_ByZipPath()
    {
        var zeta = await Mirror("sms", "sms/Z/Zeta.zip", IdentificationMethod.Manual, "a.sg", "B.SG");
        var alpha = await Mirror("sms", "sms/A/Alpha.zip", IdentificationMethod.Manual, "a.sg");
        await Mirror("sms", "sms/M/Mixed.zip", IdentificationMethod.Manual, "a.sg", "b.sms");
        await Mirror("gg", "gg/O/Other.zip", IdentificationMethod.Manual, "a.sg");
        await Mirror("sms", "sms/D/Dat.zip", IdentificationMethod.Dat, "a.sg");

        var listed = await _games.ListByOriginalExtensionAsync("sms", ".sg", default);

        Assert.That(listed.Select(g => g.Id), Is.EqualTo(new[] { alpha, zeta }));
    }
}
