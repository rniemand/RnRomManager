using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Http;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §23 <c>POST /api/games/{id}/media/retry</c> and <c>POST /api/jobs/media-retry</c> (§10.2, D38; S35).</summary>
[Category("Integration")]
public class MediaRetryEndpointsTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] Tetris = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] Zelda = new SyntheticRomBuilder { Seed = 2, Title = "ZELDA" }.Build();

    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private readonly HashSet<string> _boxArt = [];
    private readonly HashSet<string> _snaps = [];
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [SetUp]
    public async Task SetUp()
    {
        _boxArt.Clear();
        _snaps.Clear();
        var thumbnails = new StubHttpHandler(request =>
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var name = Path.GetFileNameWithoutExtension(path);
            if (path.Contains(ThumbnailKey.BoxArtFolder, StringComparison.Ordinal) && _boxArt.Contains(name))
                return StubHttpHandler.Bytes(Png(300, 400));
            if (path.Contains(ThumbnailKey.SnapFolder, StringComparison.Ordinal) && _snaps.Contains(name))
                return StubHttpHandler.Bytes(Png(160, 144));
            return StubHttpHandler.Status(HttpStatusCode.NotFound);
        });
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => services.AddHttpClient(HttpClients.Thumbnails).ConfigurePrimaryHttpMessageHandler(() => thumbnails),
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "https://thumbs.test";
        _admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await _admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        var cache = Service<DatCache>();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.GbaDat] = [],
            [IngestSeeder.N64Dat] = [],
            [IngestSeeder.NesDat] = [],
            [IngestSeeder.SnesDat] = [],
            [Gb] = [new FixtureGame("Tetris (World)", "Tetris (World).gb", Tetris), new FixtureGame("Zelda (USA)", "Zelda (USA).gb", Zelda)],
        });
        cache.Load();
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static byte[] Png(uint width, uint height)
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0, 0, 0, 0, 0, 0,
            8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "IEND"u8, 0xAE, 0x42, 0x60, 0x82];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private async Task<Guid> Ingest(string name, byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", "gb", name);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var file = new IngestFile("gb", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
        return (await Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default))!.GameId!.Value;
    }

    private async Task<string> ZipPath(Guid id) => Path.Combine(_factory.Root, "roms", (await Service<IGameRepository>().GetAsync(id, default))!.ZipPath);

    private async Task<Job> WaitForEndAsync(Guid jobId)
    {
        var jobs = Service<IJobRepository>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var job = await jobs.GetAsync(jobId, CancellationToken.None);
            if (job!.Status != JobStatus.Running || DateTime.UtcNow > deadline)
                return job;
            await Task.Delay(50);
        }
    }

    private static IEnumerable<TestCaseData> Endpoints()
    {
        yield return new TestCaseData($"/api/games/{Guid.NewGuid()}/media/retry").SetName("RoleMatrix POST media retry");
        yield return new TestCaseData("/api/jobs/media-retry").SetName("RoleMatrix POST jobs media-retry");
    }

    [TestCaseSource(nameof(Endpoints))]
    public async Task RoleMatrix_AnonymousIs401_UserIs403_XsrfRequired(string url)
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var anonymousResponse = await anonymous.PostAsync(url);
        using var userResponse = await user.PostAsync(url);
        using var noXsrf = await _admin.PostAsync(url, sendXsrf: false);

        Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(userResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await AuthClient.CodeAsync(noXsrf), Is.EqualTo("AntiforgeryInvalid"));
    }

    [Test]
    public async Task RetryMedia_FoundArt_ReturnsUpdatedGame()
    {
        var id = await Ingest("t.gb", Tetris);
        _boxArt.Add("Tetris (World)");
        _snaps.Add("Tetris (World)");

        using var response = await _admin.PostAsync($"/api/games/{id}/media/retry");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var game = await AuthClient.JsonAsync(response);
        Assert.That(game.GetProperty("hasDefaultBoxArt").GetBoolean(), Is.False);
        var images = game.GetProperty("images").EnumerateArray().ToDictionary(i => i.GetProperty("type").GetString()!, i => i.GetProperty("width").GetInt32());
        Assert.That(images, Is.EquivalentTo(new Dictionary<string, int> { ["BoxArt"] = 300, ["Snap"] = 160 }));
    }

    [Test]
    public async Task RetryMedia_NothingNew_Is200_AndTheZipIsUntouched()
    {
        var id = await Ingest("t.gb", Tetris);
        var before = await File.ReadAllBytesAsync(await ZipPath(id));

        using var response = await _admin.PostAsync($"/api/games/{id}/media/retry");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("hasDefaultBoxArt").GetBoolean(), Is.True);
        Assert.That(await File.ReadAllBytesAsync(await ZipPath(id)), Is.EqualTo(before));
    }

    [Test]
    public async Task RetryMedia_UnknownGame404_UnreadableZip409_JobRunning409()
    {
        var id = await Ingest("t.gb", Tetris);
        using (var unknown = await _admin.PostAsync($"/api/games/{Guid.NewGuid()}/media/retry"))
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using (Service<JobLock>().TryAcquire())
        {
            using var busy = await _admin.PostAsync($"/api/games/{id}/media/retry");
            using var busyJob = await _admin.PostAsync("/api/jobs/media-retry");
            Assert.That((busy.StatusCode, await AuthClient.CodeAsync(busy)), Is.EqualTo((HttpStatusCode.Conflict, "JobRunning")));
            Assert.That((busyJob.StatusCode, await AuthClient.CodeAsync(busyJob)), Is.EqualTo((HttpStatusCode.Conflict, "JobRunning")));
        }

        await File.WriteAllBytesAsync(await ZipPath(id), [1, 2, 3]);
        using var unreadable = await _admin.PostAsync($"/api/games/{id}/media/retry");
        Assert.That((unreadable.StatusCode, await AuthClient.CodeAsync(unreadable)), Is.EqualTo((HttpStatusCode.Conflict, "TargetZipUnreadable")));
    }

    [Test]
    public async Task MediaRetryJob_UpdatesWhatItFinds_SummaryCounts()
    {
        var tetris = await Ingest("t.gb", Tetris);
        await Ingest("z.gb", Zelda);
        _boxArt.Add("Tetris (World)");

        using var response = await _admin.PostAsync("/api/jobs/media-retry");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var job = await WaitForEndAsync((await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid());
        Assert.That((job.Type, job.Status), Is.EqualTo((JobType.MediaRetry, JobStatus.Completed)));
        var summary = JsonDocument.Parse(job.Summary!).RootElement;
        Assert.That(new[] { "updated", "notFound", "skipped", "error" }.Select(k => summary.GetProperty(k).GetInt32()), Is.EqualTo(new[] { 1, 1, 0, 0 }));
        Assert.That((await Service<IGameRepository>().GetAsync(tetris, default))!.HasDefaultBoxArt, Is.False);
    }
}
