using System.IO.Compression;
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

/// <summary>AU3/AU4 rows from library edits (step 20), against MariaDB + real ZIPs.</summary>
[Category("Integration")]
public class AuditLibraryEditTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] TetrisWorld = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] TetrisJapan = new SyntheticRomBuilder { Seed = 2, Title = "TETRIS" }.Build();
    private static readonly byte[] Zelda = new SyntheticRomBuilder { Seed = 4, Title = "ZELDA" }.Build();

    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private FailingMirror _mirror = null!;
    private int _bossId;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    /// <summary>The real mirror, made to fail on demand (a DB update that fails after the ZIP was written).</summary>
    private sealed class FailingMirror(ILibraryMirror inner) : ILibraryMirror
    {
        public bool Fail { get; set; }

        public Task ApplyAsync(IReadOnlyList<MirrorEntry> replace, IReadOnlyCollection<Guid> delete, CancellationToken cancellationToken) =>
            Fail ? throw new TimeoutException("DB down") : inner.ApplyAsync(replace, delete, cancellationToken);
    }

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => services.AddSingleton<ILibraryMirror>(sp =>
                _mirror = new FailingMirror(ActivatorUtilities.CreateInstance<LibraryMirror>(sp))),
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        _admin = await AuthClient.CreateAsync(_factory);
        _bossId = (await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles)).Id;
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        var cache = Service<DatCache>();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.GbaDat] = [],
            [IngestSeeder.N64Dat] = [],
            [IngestSeeder.NesDat] = [],
            [IngestSeeder.SnesDat] = [],
            [IngestSeeder.Atari2600Dat] = [],
            [IngestSeeder.ColecoVisionDat] = [],
            [IngestSeeder.GameGearDat] = [],
            [IngestSeeder.MasterSystemDat] = [],
            [IngestSeeder.MegaDriveDat] = [],
            [IngestSeeder.Sega32xDat] = [],
            [IngestSeeder.TurboGrafx16Dat] = [],
            [Gb] =
            [
                new FixtureGame("Tetris (World)", "Tetris (World).gb", TetrisWorld) { ReleaseYear = 1989 },
                new FixtureGame("Tetris (Japan)", "Tetris (Japan).gb", TetrisJapan),
                new FixtureGame("Zelda (USA)", "Zelda (USA).gb", Zelda),
            ],
        }, titleDb: IngestSeeder.EmptySwitch);
        cache.Load();
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private async Task<Guid> Ingest(string name, byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", "gb", name);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var file = new IngestFile("gb", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
        return (await Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default))!.GameId!.Value;
    }

    private async Task<long> RomId(Guid gameId, string fileName) =>
        (await Service<IRomRepository>().ListByGameAsync(gameId, default)).Single(r => r.FileName == fileName).Id;

    private sealed record Row(string Action, int? UserId, string? Username, Guid? JobId, Guid? GameId, string? Path, string? TargetPath,
        string? Details);

    /// <summary>The edit rows (the ingest rows from seeding are left out).</summary>
    private async Task<List<Row>> EditRows()
    {
        await Service<AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        return (await db.QueryAsync<Row>("""
            SELECT action AS Action, user_id AS UserId, username AS Username, job_id AS JobId, game_id AS GameId, path AS Path,
                   target_path AS TargetPath, details AS Details
            FROM audit_events WHERE action <> 'Ingested' ORDER BY id
            """)).AsList();
    }

    private static void Ok(HttpResponseMessage response)
    {
        using (response)
            Assert.That((int)response.StatusCode, Is.InRange(200, 299));
    }

    [Test]
    public async Task EachEdit_WritesExactlyItsRow_WithTheUserGameAndPaths()
    {
        var tetris = await Ingest("w.gb", TetrisWorld);
        await Ingest("j.gb", TetrisJapan);
        var zelda = await Ingest("z.gb", Zelda);

        Ok(await _admin.PostAsync($"/api/games/{tetris}/roms/{await RomId(tetris, "Tetris (Japan).gb")}/move", new { targetGameId = zelda }));
        Ok(await _admin.SendAsync(HttpMethod.Delete, $"/api/games/{zelda}/roms/{await RomId(zelda, "Tetris (Japan).gb")}", null, sendXsrf: true));
        Ok(await _admin.PostAsync($"/api/games/{tetris}/merge", new { targetGameId = zelda }));
        Ok(await _admin.SendAsync(HttpMethod.Delete, $"/api/games/{zelda}", null, sendXsrf: true));

        var rows = await EditRows();
        Assert.That(rows.All(r => r.UserId == _bossId && r.Username == "boss" && r.JobId == null), Is.True);
        Assert.That(rows.Select(r => (r.Action, r.Path, r.TargetPath, r.Details)), Is.EqualTo(new (string, string?, string?, string?)[]
        {
            (AuditActions.ReleaseMoved, "roms/gb/T/Tetris.zip", "roms/gb/Z/Zelda.zip", "Tetris (Japan).gb"),
            (AuditActions.ReleaseDeleted, "roms/gb/Z/Zelda.zip", "ingest/gb/_removed/Tetris (Japan).gb", "Tetris (Japan).gb"),
            (AuditActions.GamesMerged, "roms/gb/T/Tetris.zip", "roms/gb/Z/Zelda.zip", "1 releases, 0 images"),
            (AuditActions.GameDeleted, "roms/gb/Z/Zelda.zip", "ingest/gb/_removed/", null),
        }));
        Assert.That(rows.Select(r => r.GameId), Is.EqualTo(new Guid?[] { tetris, zelda, tetris, zelda }));
    }

    [Test]
    public async Task DeletingTheLastRelease_WritesGameDeleted_NotReleaseDeleted()
    {
        var zelda = await Ingest("z.gb", Zelda);

        Ok(await _admin.SendAsync(HttpMethod.Delete, $"/api/games/{zelda}/roms/{await RomId(zelda, "Zelda (USA).gb")}", null, sendXsrf: true));

        var row = (await EditRows()).Single();
        Assert.That((row.Action, row.Path, row.TargetPath), Is.EqualTo((AuditActions.GameDeleted, "roms/gb/Z/Zelda.zip", "ingest/gb/_removed/")));
    }

    [Test]
    public async Task Patch_RenameWritesOldAndNewPathsAndChangedFields_NoChangeAndRatingWriteNothing()
    {
        var tetris = await Ingest("w.gb", TetrisWorld);

        Ok(await _admin.PatchAsync($"/api/games/{tetris}", new { releaseYear = 1989 }));
        Ok(await _admin.PatchAsync($"/api/games/{tetris}/rating", new { rating = 4, favourite = true }));
        Assert.That(await EditRows(), Is.Empty, "a PATCH that changes nothing, and a rating, write no row");

        Ok(await _admin.PatchAsync($"/api/games/{tetris}", new { title = "Tetris DX", genres = new[] { "Puzzle" } }));

        var row = (await EditRows()).Single();
        Assert.That((row.Action, row.GameId, row.Path, row.TargetPath, row.Details),
            Is.EqualTo((AuditActions.GameUpdated, (Guid?)tetris, "roms/gb/T/Tetris.zip", "roms/gb/T/Tetris DX.zip", "title, genres")));
    }

    [Test]
    public async Task ReleaseDelete_WithTheEntryMissingFromTheZip_HasANullTarget()
    {
        var tetris = await Ingest("w.gb", TetrisWorld);
        await Ingest("j.gb", TetrisJapan);
        using (var zip = ZipFile.Open(Path.Combine(_factory.Root, "roms", "gb", "T", "Tetris.zip"), ZipArchiveMode.Update))
            zip.GetEntry("roms/Tetris (Japan).gb")!.Delete();

        Ok(await _admin.SendAsync(HttpMethod.Delete, $"/api/games/{tetris}/roms/{await RomId(tetris, "Tetris (Japan).gb")}", null, sendXsrf: true));

        var row = (await EditRows()).Single();
        Assert.That((row.Action, row.TargetPath, row.Details), Is.EqualTo((AuditActions.ReleaseDeleted, (string?)null, "Tetris (Japan).gb")));
    }

    [Test]
    public async Task AMergeWhoseDbUpdateFails_StillWritesGamesMerged_AndTheGroupMergeCountsIt()
    {
        var tetris = await Ingest("w.gb", TetrisWorld);
        var zelda = await Ingest("z.gb", Zelda);
        _mirror.Fail = true;

        using var response = await _admin.PostAsync("/api/reports/duplicates/merge", new { targetGameId = zelda, sourceGameIds = new[] { tetris } });

        var problem = await AuthClient.JsonAsync(response);
        Assert.That((response.StatusCode, problem.GetProperty("code").GetString()), Is.EqualTo((HttpStatusCode.InternalServerError, "DbWriteFailed")));
        Assert.That(problem.GetProperty("merged").EnumerateArray().Select(m => m.GetGuid()), Is.EqualTo(new[] { tetris }));
        _mirror.Fail = false;
        var row = (await EditRows()).Single();
        Assert.That((row.Action, row.GameId, row.Path), Is.EqualTo((AuditActions.GamesMerged, (Guid?)tetris, "roms/gb/T/Tetris.zip")));
    }
}
