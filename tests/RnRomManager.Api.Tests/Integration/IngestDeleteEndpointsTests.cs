using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Media;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>D1 <c>GET /api/ingest/failed</c> and D2 <c>POST /api/ingest/delete</c>, against MariaDB.</summary>
[Category("Integration")]
public class IngestDeleteEndpointsTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] Known = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] Unknown = new SyntheticRomBuilder { Seed = 929, Title = "TETRIS" }.Build();
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();
    private string Ingest(params string[] relative) => Path.Combine([_factory.Root, "ingest", .. relative]);

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
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
            [IngestSeeder.Atari2600Dat] = [],
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
            [Gb] = [new FixtureGame("Tetris (World)", "Tetris (World).gb", Known)],
            ["Nintendo - Game Boy Color"] = [],
        }, titleDb: IngestSeeder.EmptySwitch, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private async Task<IngestFile> Process(string name, byte[] bytes)
    {
        var path = Ingest("gb", name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var file = new IngestFile("gb", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
        await Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), CancellationToken.None);
        return file;
    }

    private Task<HttpResponseMessage> Delete(params object[] files) => _admin.PostAsync("/api/ingest/delete", new { files });

    private async Task<List<(string Action, string? SystemId, string? Path, string? Details)>> DeleteAuditRows()
    {
        await Service<AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        return (await db.QueryAsync<(string, string?, string?, string?)>(
            "SELECT action, system_id, path, details FROM audit_events WHERE action = 'IngestFileDeleted' ORDER BY id")).ToList();
    }

    [Test]
    public async Task Failed_ListsAFailedFile_WithItsReason()
    {
        await Process("tiny.gb", [1, 2, 3, 4]);

        using var response = await _admin.GetAsync("/api/ingest/failed");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var systems = (await AuthClient.JsonAsync(response)).EnumerateArray().ToList();
        Assert.That(systems[0].GetProperty("system").GetString(), Is.EqualTo("gb"));
        var file = systems[0].GetProperty("files").EnumerateArray().Single();
        Assert.That((file.GetProperty("name").GetString(), file.GetProperty("reason").GetString(), file.GetProperty("sizeBytes").GetInt64()),
            Is.EqualTo(("tiny.gb", IngestFailures.TooSmall, 4L)));
        Assert.That(systems.Skip(1).SelectMany(s => s.GetProperty("files").EnumerateArray()), Is.Empty);
    }

    [Test]
    public async Task PlainUser_Is403_OnBothRoutes()
    {
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();

        using var get = await user.GetAsync("/api/ingest/failed");
        using var post = await user.PostAsync("/api/ingest/delete", new { files = new[] { new { system = "gb", folder = "pending", name = "a.gb" } } });

        Assert.That((get.StatusCode, post.StatusCode), Is.EqualTo((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)));
    }

    [TestCase("[]")]
    [TestCase("""[{"system":"nope","folder":"pending","name":"a.gb"}]""")]
    [TestCase("""[{"system":"gb","folder":"pending","name":"../x.gb"}]""")]
    [TestCase("""[{"system":"gb","folder":"removed","name":"a.gb"}]""")]
    public async Task InvalidRequest_Is400_AndDeletesNothing(string files)
    {
        var keep = Ingest("gb", "a.gb");
        File.WriteAllBytes(keep, [1]);

        using var response = await Delete([.. JsonSerializer.Deserialize<JsonElement[]>(files)!.Cast<object>()]);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty("files", out _), Is.True);
        Assert.That(File.Exists(keep), Is.True);
    }

    [Test]
    public async Task MissingBody_Is400()
    {
        using var response = await _admin.PostAsync("/api/ingest/delete");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task PendingReviewFile_IsDeleted_WithItsPendingReviewRows_AndAudited()
    {
        var file = await Process("mystery.gb", Unknown);
        var reviews = Service<IIngestReviewRepository>();
        Assert.That(await reviews.ListPendingSourcesAsync(default), Has.Count.EqualTo(1));

        using var response = await Delete(new { system = "gb", folder = "pending", name = "mystery.gb" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(response);
        Assert.That((body.GetProperty("deleted").GetInt32(), body.GetProperty("failed").GetArrayLength()), Is.EqualTo((1, 0)));
        Assert.That(File.Exists(file.FullPath), Is.False);
        Assert.That(await reviews.ListPendingSourcesAsync(default), Is.Empty);
        Assert.That(await DeleteAuditRows(),
            Is.EqualTo(new[] { ("IngestFileDeleted", (string?)"gb", (string?)"ingest/gb/mystery.gb", (string?)"pending") }));
    }

    [Test]
    public async Task FailedFile_IsDeleted_WithItsSidecar()
    {
        await Process("tiny.gb", [1, 2, 3, 4]);
        var failed = Ingest("gb", "_failed", "tiny.gb");
        Assert.That(File.Exists(failed + ".error.txt"), Is.True);

        using var response = await Delete(new { system = "gb", folder = "failed", name = "tiny.gb" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(File.Exists(failed) || File.Exists(failed + ".error.txt"), Is.False);
        Assert.That(await DeleteAuditRows(),
            Is.EqualTo(new[] { ("IngestFileDeleted", (string?)"gb", (string?)"ingest/gb/_failed/tiny.gb", (string?)"failed") }));
    }

    [Test]
    public async Task WhileAJobHoldsTheLock_Is409JobRunning()
    {
        var keep = Ingest("gb", "a.gb");
        File.WriteAllBytes(keep, [1]);
        using var held = Service<JobLock>().TryAcquire();

        using var response = await Delete(new { system = "gb", folder = "pending", name = "a.gb" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("JobRunning"));
        Assert.That(File.Exists(keep), Is.True);
    }
}

/// <summary>Spec G4 through the API: an external system's ingest files behind a link are neither listed nor deleted.</summary>
[Category("Integration")]
public class IngestDeleteExternalEndpointsTests
{
    [Test]
    public async Task LinkedFailedFolder_ListsNothing_AndDeleteReportsTheFileAsFailed()
    {
        using var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => RnRomManager.Core.Systems.SystemHandlerRegistration.AddSystemHandler<ExternalTestHandler>(services),
        };
        using var admin = await AuthClient.CreateAsync(factory);
        await TestUsers.CreateAsync(factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        var outside = Directory.CreateDirectory(Path.Combine(factory.Root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "a.nsp"), "theirs");
        var link = Path.Combine(factory.Root, "ingest", ExternalTestHandler.SystemId, "_failed");
        Directory.Delete(link);
        Assert.That(TestLinks.TryLinkDirectory(link, outside), Is.True, "this account can't create a directory link");
        try
        {
            using var list = await admin.GetAsync("/api/ingest/failed");
            Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await list.Content.ReadAsStringAsync(), Does.Not.Contain("a.nsp"));

            using var response = await admin.PostAsync("/api/ingest/delete",
                new { files = new[] { new { system = ExternalTestHandler.SystemId, folder = "failed", name = "a.nsp" } } });
            var body = await AuthClient.JsonAsync(response);

            Assert.That((body.GetProperty("deleted").GetInt32(), body.GetProperty("failed").GetArrayLength()), Is.EqualTo((0, 1)));
            Assert.That(File.ReadAllText(Path.Combine(outside, "a.nsp")), Is.EqualTo("theirs"));
        }
        finally
        {
            TestLinks.RemoveDirectoryLink(link); // or the factory's cleanup would follow it
        }
    }
}
