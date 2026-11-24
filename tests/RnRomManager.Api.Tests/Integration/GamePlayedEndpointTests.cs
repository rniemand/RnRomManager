using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Users;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>UI polish B22: <c>POST /api/games/{id}/roms/{romId}/played</c> records one <c>GamePlayed</c> row.</summary>
[Category("Integration")]
public class GamePlayedEndpointTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] Tetris = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] Zelda = new SyntheticRomBuilder { Seed = 4, Title = "ZELDA" }.Build();

    private ApiFactory _factory = null!;
    private Guid _tetris;
    private Guid _zelda;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        await _factory.ResetAsync();
        await TestUsers.CreateAsync(_factory, "site", Roles.None);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        var cache = Service<DatCache>();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.GbaDat] = [],
            [IngestSeeder.N64Dat] = [],
            [IngestSeeder.NesDat] = [],
            [IngestSeeder.SnesDat] = [],
            [IngestSeeder.Atari2600Dat] = [],
            [IngestSeeder.LynxDat] = [],
            [IngestSeeder.JaguarDat] = [],
            [IngestSeeder.VirtualBoyDat] = [],
            [IngestSeeder.Atari5200Dat] = [],
            [IngestSeeder.Atari7800Dat] = [],
            [IngestSeeder.ColecoVisionDat] = [],
            [IngestSeeder.GameGearDat] = [],
            [IngestSeeder.MasterSystemDat] = [],
            [IngestSeeder.MegaDriveDat] = [],
            [IngestSeeder.Sega32xDat] = [],
            [IngestSeeder.TurboGrafx16Dat] = [],
            [IngestSeeder.PsxDat] = [],
            [IngestSeeder.PspDat] = [],
            [IngestSeeder.GameCubeDat] = [],
            [IngestSeeder.WiiDat] = [],
            [IngestSeeder.Xbox360Dat] = [],
            [IngestSeeder.SaturnDat] = [],
            [IngestSeeder.SegaCdDat] = [],
            [IngestSeeder.ThreeDoDat] = [],
            [IngestSeeder.C64Dat] = [],
            [IngestSeeder.PetDat] = [],
            [IngestSeeder.Vic20Dat] = [], [IngestSeeder.SatellaviewDat] = [], [IngestSeeder.SufamiTurboDat] = [], [IngestSeeder.FdsDat] = [],
            [IngestSeeder.AmigaDat] = [], [IngestSeeder.ThreeDsDat] = [], [IngestSeeder.TurboGrafxCdDat] = [], [IngestSeeder.NeoGeoPocketDat] = [], [IngestSeeder.NeoGeoPocketColorDat] = [],
            [IngestSeeder.NintendoDsDat] = [],
            [Gb] =
            [
                new FixtureGame("Tetris (World)", "Tetris (World).gb", Tetris),
                new FixtureGame("Zelda (USA)", "Zelda (USA).gb", Zelda),
            ],
        }, titleDb: IngestSeeder.EmptySwitch, mame: IngestSeeder.MinimalMame, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
        _tetris = await Ingest("t.gb", Tetris);
        _zelda = await Ingest("z.gb", Zelda);
    }

    private async Task<Guid> Ingest(string name, byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", "gb", name);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var file = new IngestFile("gb", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
        return (await Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default))!.GameId!.Value;
    }

    private async Task<long> RomId(Guid gameId) => (await Service<IRomRepository>().ListByGameAsync(gameId, default)).Single().Id;

    private async Task<AuthClient> Login(string username)
    {
        var client = await AuthClient.CreateAsync(_factory);
        (await client.LoginAsync(username, TestUsers.Password)).Dispose();
        return client;
    }

    private sealed record Row(string Action, string? Username, string? SystemId, Guid? GameId, string? Path, string? Details);

    private async Task<List<Row>> PlayedRows()
    {
        await Service<AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        return (await db.QueryAsync<Row>("""
            SELECT action AS Action, username AS Username, system_id AS SystemId, game_id AS GameId, path AS Path, details AS Details
            FROM audit_events WHERE action = 'GamePlayed' ORDER BY id
            """)).AsList();
    }

    [TestCase("site")]
    [TestCase("boss")]
    public async Task Played_Returns204_AndRecordsOneRow(string username)
    {
        using var client = await Login(username);
        var zipPath = (await Service<IGameRepository>().GetAsync(_tetris, default))!.ZipPath;

        using (var response = await client.PostAsync($"/api/games/{_tetris}/roms/{await RomId(_tetris)}/played"))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        Assert.That(await PlayedRows(), Is.EqualTo(new[]
        {
            new Row(AuditActions.GamePlayed, username, "gb", _tetris, AuditPaths.Library(zipPath), "Tetris (World).gb"),
        }));
    }

    [Test]
    public async Task UnknownGame_OrForeignRelease_Is404_AndRecordsNothing()
    {
        using var client = await Login("site");

        using (var unknown = await client.PostAsync($"/api/games/{Guid.NewGuid()}/roms/{await RomId(_tetris)}/played"))
        {
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await AuthClient.CodeAsync(unknown), Is.EqualTo("NotFound"));
        }
        using (var foreign = await client.PostAsync($"/api/games/{_tetris}/roms/{await RomId(_zelda)}/played"))
            Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        Assert.That(await PlayedRows(), Is.Empty);
    }

    [Test]
    public async Task Anonymous_Is401()
    {
        using var client = await AuthClient.CreateAsync(_factory);
        using var response = await client.PostAsync($"/api/games/{_tetris}/roms/{await RomId(_tetris)}/played");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
