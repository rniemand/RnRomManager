using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ClearExtensions;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Admin actions R1/R4: <c>POST /api/jobs/igdb-sync</c>, with the IGDB client replaced.</summary>
[Category("Integration")]
public class IgdbSyncEndpointTests
{
    private static readonly byte[] Tetris = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();

    private readonly IIgdbGames _igdb = Substitute.For<IIgdbGames>();
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync()) { TestServices = s => s.AddSingleton(_igdb) };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        await _factory.ResetAsync();
        _igdb.ClearSubstitute();
        _igdb.Configured.Returns(true);
        _admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        var cache = Service<DatCache>();
        FixtureDatWriter.Write(cache.Directory, IngestSeeder.EmptyDats("Nintendo - Game Boy", [new FixtureGame("Tetris (World)", "Tetris (World).gb", Tetris)]),
            titleDb: IngestSeeder.EmptySwitch, mame: IngestSeeder.MinimalMame, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
    }

    [TearDown]
    public void TearDown() => _admin.Dispose();

    private static byte[] Png(uint width, uint height)
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0, 0, 0, 0, 0, 0,
            8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "IEND"u8, 0xAE, 0x42, 0x60, 0x82];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private async Task<Guid> IngestAsync()
    {
        var path = Path.Combine(_factory.Root, "ingest", "gb", "t.gb");
        await File.WriteAllBytesAsync(path, Tetris);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var file = new IngestFile("gb", path, "t.gb", Tetris.Length, File.GetLastWriteTimeUtc(path));
        return (await Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default))!.GameId!.Value;
    }

    private async Task<Job> WaitForEndAsync(Guid jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var job = await Service<IJobRepository>().GetAsync(jobId, CancellationToken.None);
            if (job!.Status != JobStatus.Running || DateTime.UtcNow > deadline)
                return job;
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task Start_Is202_AndTheDetailShowsTheRating_NoZipWritten()
    {
        var id = await IngestAsync();
        Assert.That((await Service<LibraryEditor>().PatchAsync(id, new GamePatch { SetIgdbId = true, IgdbId = 42 }, null, default)).Code, Is.Null);
        var zip = Path.Combine(_factory.Root, "roms", (await Service<IGameRepository>().GetAsync(id, default))!.ZipPath);
        var mtime = File.GetLastWriteTimeUtc(zip);
        _igdb.GetGamesAsync(default!, default).ReturnsForAnyArgs([new IgdbGameRating(42, null, 81.6, 1234)]);

        using var response = await _admin.PostAsync("/api/jobs/igdb-sync");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var job = await WaitForEndAsync((await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid());
        Assert.That((job.Type, job.Status, job.Summary), Is.EqualTo((JobType.IgdbSync, JobStatus.Completed, """{"updated":1,"noRating":0,"notFound":0}""")));
        using var detail = await _admin.GetAsync($"/api/games/{id}");
        var rating = (await AuthClient.JsonAsync(detail)).GetProperty("igdbRating");
        Assert.That((rating.GetProperty("score").GetDouble(), rating.GetProperty("votes").GetInt32()), Is.EqualTo((81.6, 1234)));
        Assert.That(rating.GetProperty("syncedAt").GetString(), Does.EndWith("Z"));
        Assert.That(File.GetLastWriteTimeUtc(zip), Is.EqualTo(mtime));
    }

    [Test]
    public async Task NotConfigured_Is409_JobRunning_Is409()
    {
        _igdb.Configured.Returns(false);
        using (var response = await _admin.PostAsync("/api/jobs/igdb-sync"))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.That((problem.GetProperty("code").GetString(), problem.GetProperty("title").GetString()),
                Is.EqualTo(("IgdbNotConfigured", "Refreshing ratings needs Sources:IgdbClientId and Sources:IgdbClientSecret.")));
        }

        _igdb.Configured.Returns(true);
        using (Service<JobLock>().TryAcquire("test"))
        {
            using var busy = await _admin.PostAsync("/api/jobs/igdb-sync");
            Assert.That((busy.StatusCode, await AuthClient.CodeAsync(busy)), Is.EqualTo((HttpStatusCode.Conflict, "JobRunning")));
        }
    }

    [Test]
    public async Task RoleMatrix_AnonymousIs401_UserIs403_XsrfRequired()
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var anonymousResponse = await anonymous.PostAsync("/api/jobs/igdb-sync");
        using var userResponse = await user.PostAsync("/api/jobs/igdb-sync");
        using var noXsrf = await _admin.PostAsync("/api/jobs/igdb-sync", sendXsrf: false);

        Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(userResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await AuthClient.CodeAsync(noXsrf), Is.EqualTo("AntiforgeryInvalid"));
    }
}