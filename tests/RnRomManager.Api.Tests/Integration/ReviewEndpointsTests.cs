using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

[Category("Integration")]
public class ReviewEndpointsTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] Known = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] Unknown = new SyntheticRomBuilder { Seed = 930, Title = "MYSTERY" }.Build();
    private static readonly byte[] Color = new SyntheticRomBuilder { Seed = 932, CgbFlag = 0xC0 }.Build();
    private ApiFactory _factory = null!;
    private AuthClient _admin = null!;
    private IngestPipeline? _pipelineOverride;
    private int _adminId;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();
    private IIngestReviewRepository Reviews => Service<IIngestReviewRepository>();

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => services.AddTransient(sp => ActivatorUtilities.CreateInstance<ReviewResolver>(sp,
                _pipelineOverride ?? sp.GetRequiredService<IngestPipeline>())),
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        _pipelineOverride = null;
        await _factory.ResetAsync();
        _admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        using var login = await _admin.LoginAsync("boss", TestUsers.Password);
        _adminId = (await Service<IUserRepository>().GetByUsernameAsync("boss", default))!.Id;
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
            [Gb] = [new FixtureGame("Tetris (World)", "Tetris (World).gb", Known)
            { ReleaseYear = 1989, Developers = ["Nintendo"], Publishers = ["Nintendo"], Genres = ["Puzzle"] }],
            ["Nintendo - Game Boy Color"] = [new FixtureGame("Color Game (World)", "Color Game (World).gbc", Color)],
        }, titleDb: IngestSeeder.EmptySwitch, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
    }

    [TearDown]
    public void TearDown() { _admin.Dispose(); }

    private IngestFile Drop(string name, byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", "gb", name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return new IngestFile("gb", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
    }

    private Task<IngestItemResult?> Ingest(IngestFile file) => Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default);

    private async Task RunIngest() => await ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, Substitute.For<IJobItemRepository>())
        .RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);

    private async Task<ReviewItem> Queue(bool archive = false, string name = "mystery.gb", byte[]? bytes = null)
    {
        bytes ??= Unknown;
        if (archive)
        {
            var path = Path.Combine(_factory.Root, "ingest", "gb", "pack.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var entry = zip.CreateEntry("nested/" + name).Open();
                entry.Write(bytes);
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
            await RunIngest();
        }
        else
            await Ingest(Drop(name, bytes));
        return (await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!;
    }

    private async Task<Guid> KnownGame() => (await Ingest(Drop("tetris.gb", Known)))!.GameId!.Value;

    private async Task<InfoJsonGame> ReadGame(Guid id)
    {
        var row = (await Service<IGameRepository>().GetAsync(id, default))!;
        using var zip = LibraryZip.Open(Path.Combine(_factory.Root, "roms", row.ZipPath), Service<InfoJsonReader>());
        return zip.InfoJson!.Game!;
    }

    private static object Manual(string title = "My Game") => new { action = "Manual", title, regions = new[] { "USA", "Europe" }, languages = new[] { "En" }, revision = "Rev 2", flags = new[] { "Homebrew" } };

    [TestCase(false)]
    [TestCase(true)]
    public async Task DatSelection_IsUnverified_UsesDatMetadataAndActualHashes(bool archive)
    {
        var item = await Queue(archive);
        var response = await _admin.ResolveAsync(item.Id, new { action = "Dat", datSystem = "gb", datGameName = "Tetris (World)" });
        Assert.That(response.Code, Is.Null);
        var id = response.GameId!.Value;
        var game = await ReadGame(id);
        var rom = game.Roms.Single();
        Assert.That((rom.BaseTitle, rom.FileName, rom.Identification.Method), Is.EqualTo(("Tetris", "Tetris (World) (Unverified).gb", IdentificationMethod.Manual)));
        Assert.That(rom.Flags, Is.EqualTo(new[] { "Unverified" }));
        Assert.That(rom.Regions, Is.EqualTo(new[] { "World" }));
        Assert.That((rom.Identification.DatName, rom.Identification.DatVersion, rom.Identification.DatGameName), Is.EqualTo((Gb, FixtureDatWriter.Version, "Tetris (World)")));
        Assert.That((rom.Metadata.Sha1, rom.Metadata.Crc32, rom.Metadata.SourcePath), Is.EqualTo((item.Sha1, item.Crc32, item.SourcePath)));
        Assert.That((game.ReleaseYear, game.Developers.Single()), Is.EqualTo((1989, "Nintendo")));
        var resolved = (await Reviews.GetAsync(item.Id, default))!;
        Assert.That((resolved.Status, resolved.GameId, resolved.ResolvedBy), Is.EqualTo(("Resolved", (Guid?)id, (int?)_adminId)));
        Assert.That(resolved.ResolvedAt, Is.Not.Null);
        Assert.That(File.Exists(item.SourcePath), Is.EqualTo(archive));
        if (archive)
        {
            await RunIngest();
            Assert.That(File.Exists(item.SourcePath), Is.False, "resolved archive is deleted on the next ingest");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ManualEntry_UsesProvidedTagsAndSanitizedName_NoDatMetadata(bool archive)
    {
        var item = await Queue(archive);
        var response = await _admin.ResolveAsync(item.Id, Manual("My: Game"));
        Assert.That(response.Code, Is.Null);
        var id = response.GameId!.Value;
        var game = await ReadGame(id);
        var rom = game.Roms.Single();
        Assert.That((game.Title, rom.BaseTitle, rom.FileName), Is.EqualTo(("My: Game", "My: Game", "My_ Game (USA, Europe) (Rev 2) (Homebrew).gb")));
        Assert.That(rom.Languages, Is.EqualTo(new[] { "En" }));
        Assert.That(rom.Identification, Is.EqualTo(new InfoJsonIdentification(IdentificationMethod.Manual, null, null, null)));
        Assert.That(game.ReleaseYear, Is.Null);
        Assert.That(game.Developers, Is.Empty);
        Assert.That(File.Exists(item.SourcePath), Is.EqualTo(archive));
    }

    [TestCase("Hack", false)]
    [TestCase("Translated", false)]
    [TestCase("Hack", true)]
    [TestCase("Translated", true)]
    public async Task Attach_UsesTargetTitleAndOnlyChosenFlag_PreservesVerifiedPrimary(string flag, bool archive)
    {
        var target = await KnownGame();
        var item = await Queue(archive, "mystery (Japan) (Rev 1).gb");
        var response = await _admin.ResolveAsync(item.Id, new { action = "Attach", targetGameId = target, flag });
        Assert.That(response.Code, Is.Null);
        Assert.That(response.GameId!.Value, Is.EqualTo(target));
        var game = await ReadGame(target);
        var rom = game.Roms.Single(r => r.Metadata.Sha1 == item.Sha1);
        Assert.That((rom.BaseTitle, rom.FileName, rom.Revision), Is.EqualTo(("Tetris", $"mystery (Japan) (Rev 1) ({flag}).gb", (string?)null)));
        Assert.That(rom.Regions, Is.Empty);
        Assert.That(rom.Languages, Is.Empty);
        Assert.That(rom.Flags, Is.EqualTo(new[] { flag }));
        Assert.That(rom.IsPrimary, Is.False);
        Assert.That(game.Roms.Single(r => r.IsPrimary).Identification.Method, Is.EqualTo(IdentificationMethod.Dat));
        Assert.That(rom.Identification.DatName, Is.Null);
        Assert.That(File.Exists(item.SourcePath), Is.EqualTo(archive));
    }

    private async Task<ReviewItem> Ambiguous(ReviewItem item, params Guid[] candidates)
    {
        var update = item with { Reason = "AmbiguousGroup", Suggestions = "[]", CandidateGameIds = JsonSerializer.Serialize(candidates),
            DatIdentification = JsonSerializer.Serialize(new { datName = Gb, datVersion = "queued-version", datGameName = "Mystery (USA)", romName = "Mystery (USA).gb" }) };
        await Reviews.UpsertAsync(update, default);
        return (await Reviews.GetAsync(item.Id, default))!;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AttachNone_KeepsQueuedDatIdentityAndFilename_InChosenCandidate(bool archive)
    {
        var target = await KnownGame();
        var item = await Ambiguous(await Queue(archive), target, Guid.NewGuid());
        var response = await _admin.ResolveAsync(item.Id, new { action = "Attach", targetGameId = target, flag = "None" });
        Assert.That(response.Code, Is.Null);
        var rom = (await ReadGame(target)).Roms.Single(r => r.Metadata.Sha1 == item.Sha1);
        Assert.That((rom.BaseTitle, rom.FileName), Is.EqualTo(("Mystery", "Mystery (USA).gb")));
        Assert.That(rom.Identification, Is.EqualTo(new InfoJsonIdentification(IdentificationMethod.Dat, Gb, "queued-version", "Mystery (USA)")));
        Assert.That(rom.Flags, Is.Empty);
        Assert.That(File.Exists(item.SourcePath), Is.EqualTo(archive));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Dismiss_MovesOnlyLooseFiles_RecordsResolver(bool archive)
    {
        var item = await Queue(archive);
        using var response = await _admin.PostAsync($"/api/review/{item.Id}/dismiss");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        var row = (await Reviews.GetAsync(item.Id, default))!;
        Assert.That((row.Status, row.ResolvedBy, row.GameId), Is.EqualTo(("Dismissed", (int?)_adminId, (Guid?)null)));
        Assert.That(row.ResolvedAt, Is.Not.Null);
        Assert.That(File.Exists(item.SourcePath), Is.EqualTo(archive));
        if (archive)
        {
            await RunIngest();
            Assert.That(File.Exists(Path.Combine(_factory.Root, "ingest", "gb", "_failed", "pack.zip")), Is.True);
        }
        else
            Assert.That(File.ReadAllText(Path.Combine(_factory.Root, "ingest", "gb", "_failed", item.OriginalFileName + ".error.txt")), Does.Contain("Dismissed"));
    }

    [TestCase("resolve", false)]
    [TestCase("resolve", true)]
    [TestCase("dismiss", false)]
    [TestCase("dismiss", true)]
    public async Task MissingSource_Is409_DeletesReview_AndReleasesLock(string action, bool archive)
    {
        var item = await Queue(archive);
        File.Delete(item.SourcePath);
        // B5: a resolve reports SourceMissing in its job summary; Dismiss stays a synchronous 409.
        string? code;
        if (action == "resolve")
        {
            var result = await _admin.ResolveAsync(item.Id, Manual());
            Assert.That(result.Status, Is.EqualTo(HttpStatusCode.Accepted));
            code = result.Code;
        }
        else
        {
            using var response = await _admin.PostAsync($"/api/review/{item.Id}/dismiss");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            code = await AuthClient.CodeAsync(response);
        }
        Assert.That(code, Is.EqualTo("SourceMissing"));
        Assert.That(await Reviews.GetAsync(item.Id, default), Is.Null);
        await WaitIdleAsync();
        using var released = Service<JobLock>().TryAcquire();
        Assert.That(released, Is.Not.Null);
    }

    [TestCase("resolve")]
    [TestCase("dismiss")]
    public async Task LockBusy_Is409_LeavesItemPending(string action)
    {
        var item = await Queue();
        using var held = Service<JobLock>().TryAcquire();
        using var response = await _admin.PostAsync($"/api/review/{item.Id}/{action}", action == "resolve" ? Manual() : null);
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("JobRunning"));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_Is204_RemovesTheSourceAndItsPendingRows(bool archive)
    {
        var item = await Queue(archive);
        using var response = await _admin.SendAsync(HttpMethod.Delete, $"/api/review/{item.Id}", null, true);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(File.Exists(item.ArchivePath ?? item.SourcePath), Is.False);
        Assert.That(await Reviews.GetAsync(item.Id, default), Is.Null);
        Assert.That(File.Exists(Path.Combine(_factory.Root, "ingest", "gb", "_failed", Path.GetFileName(item.ArchivePath ?? item.SourcePath))), Is.False);
    }

    [Test]
    public async Task Delete_UnknownOrDone_Is404_LockBusy_Is409()
    {
        var item = await Queue();
        using var unknown = await _admin.SendAsync(HttpMethod.Delete, "/api/review/999999", null, true);
        using (Service<JobLock>().TryAcquire())
        {
            using var busy = await _admin.SendAsync(HttpMethod.Delete, $"/api/review/{item.Id}", null, true);
            Assert.That(busy.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(busy), Is.EqualTo("JobRunning"));
        }
        (await _admin.PostAsync($"/api/review/{item.Id}/dismiss")).Dispose();
        using var dismissed = await _admin.SendAsync(HttpMethod.Delete, $"/api/review/{item.Id}", null, true);

        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(dismissed.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Delete_RequiresAntiforgery()
    {
        var item = await Queue();
        using var response = await _admin.SendAsync(HttpMethod.Delete, $"/api/review/{item.Id}", null, sendXsrf: false);
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("AntiforgeryInvalid"));
        Assert.That(File.Exists(item.SourcePath), Is.True);
    }

    [TestCase("GET", "/api/review")]
    [TestCase("GET", "/api/review/1")]
    [TestCase("POST", "/api/review/1/resolve")]
    [TestCase("POST", "/api/review/1/dismiss")]
    [TestCase("DELETE", "/api/review/1")]
    [TestCase("GET", "/api/dat/search?system=gb&q=tetris")]
    public async Task RoleMatrix_Anonymous401_User403(string method, string url)
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();
        using var anonResponse = await anonymous.SendAsync(new HttpMethod(method), url, Manual(), true);
        using var userResponse = await user.SendAsync(new HttpMethod(method), url, Manual(), true);
        Assert.That(anonResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(userResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [TestCase("resolve")]
    [TestCase("dismiss")]
    public async Task Mutations_RequireAntiforgery(string action)
    {
        var item = await Queue();
        using var response = await _admin.PostAsync($"/api/review/{item.Id}/{action}", action == "resolve" ? Manual() : null, sendXsrf: false);
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("AntiforgeryInvalid"));
    }

    [Test]
    public async Task ListAndDetail_ReturnPagingHashesStructuredJsonAndCandidateNames()
    {
        var target = await KnownGame();
        var first = await Queue();
        var second = await Queue(name: "second.gb", bytes: new SyntheticRomBuilder { Seed = 931, Title = "SECOND" }.Build());
        await Ambiguous(second, target);
        using var list = await _admin.GetAsync("/api/review?pageSize=1&page=2");
        var page = await AuthClient.JsonAsync(list);
        Assert.That((page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt32()), Is.EqualTo((2, 1, 2)));
        var summary = page.GetProperty("items")[0];
        Assert.That((summary.GetProperty("id").GetInt64(), summary.GetProperty("fileName").GetString(), summary.GetProperty("headerTitle").GetString()), Is.EqualTo((second.Id, "second.gb", "SECOND")));
        using var detail = await _admin.GetAsync($"/api/review/{second.Id}");
        var data = await AuthClient.JsonAsync(detail);
        Assert.That(data.GetProperty("candidateGames")[0].GetProperty("displayTitle").GetString(), Is.EqualTo("Tetris"));
        Assert.That(data.GetProperty("datIdentification").GetProperty("datVersion").GetString(), Is.EqualTo("queued-version"));
        Assert.That(data.GetProperty("sha1").GetString(), Is.EqualTo(second.Sha1));
        await _admin.PostAsync($"/api/review/{first.Id}/dismiss");
        using var dismissed = await _admin.GetAsync("/api/review?status=Dismissed");
        Assert.That((await AuthClient.JsonAsync(dismissed)).GetProperty("total").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public async Task Audit_ResolveDismissDelete_RecordTheSignedInUser_ResolveWithItsJob()
    {
        var resolved = await Queue();
        var dismissed = await Queue(name: "second.gb", bytes: new SyntheticRomBuilder { Seed = 931, Title = "SECOND" }.Build());
        var deleted = await Queue(name: "third.gb", bytes: new SyntheticRomBuilder { Seed = 934, Title = "THIRD" }.Build());

        await _admin.ResolveAsync(resolved.Id, Manual());
        (await _admin.PostAsync($"/api/review/{dismissed.Id}/dismiss")).Dispose();
        (await _admin.SendAsync(HttpMethod.Delete, $"/api/review/{deleted.Id}", null, sendXsrf: true)).Dispose();

        await Service<RnRomManager.Api.AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        var rows = (await db.QueryAsync<(string Action, int? UserId, string? Username, Guid? JobId, string? Path, string? Details)>(
            "SELECT action, user_id, username, job_id, path, details FROM audit_events ORDER BY id")).ToList();
        var jobId = await db.QuerySingleAsync<Guid>("SELECT id FROM jobs WHERE type = 'ReviewResolve'");
        Assert.That(rows, Is.EqualTo(new (string, int?, string?, Guid?, string?, string?)[]
        {
            ("Ingested", _adminId, "boss", jobId, "ingest/gb/mystery.gb", "review resolve (Manual)"),
            ("MovedToFailed", _adminId, "boss", null, "ingest/gb/second.gb", "review dismiss"),
            ("ReviewDeleted", _adminId, "boss", null, "ingest/gb/third.gb", null),
        }));
        Assert.That(File.ReadAllText(Path.Combine(_factory.Root, "ingest", "gb", "_failed", "second.gb.error.txt")),
            Does.Contain("job: review by boss"));
    }

    [Test]
    public async Task List_IsArchive_AndDetail_SystemName()
    {
        var loose = await Queue();
        var packed = await Queue(archive: true, name: "packed.gb", bytes: new SyntheticRomBuilder { Seed = 933, Title = "PACKED" }.Build());

        using var list = await _admin.GetAsync("/api/review");
        var items = (await AuthClient.JsonAsync(list)).GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetInt64(), i => i.GetProperty("isArchive").GetBoolean());
        Assert.That((items[loose.Id], items[packed.Id]), Is.EqualTo((false, true)));

        using var detail = await _admin.GetAsync($"/api/review/{loose.Id}");
        Assert.That((await AuthClient.JsonAsync(detail)).GetProperty("systemName").GetString(), Is.EqualTo("Game Boy"));
    }

    [TestCase("/api/review?status=Bad")]
    [TestCase("/api/review?page=0")]
    [TestCase("/api/review?pageSize=201")]
    [TestCase("/api/dat/search?system=bad&q=x")]
    [TestCase("/api/dat/search?system=gb")]
    [TestCase("/api/dat/search?system=gb&q=x&limit=21")]
    public async Task InvalidQuery_IsValidationProblem(string url)
    {
        using var response = await _admin.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await AuthClient.JsonAsync(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
        Assert.That(problem.GetProperty("errors").EnumerateObject(), Is.Not.Empty);
    }

    [Test]
    public async Task Resolve_UpdateOnANonDatAction_Is400WithAnUpdateFieldError()
    {
        using var response = await _admin.PostAsync("/api/review/1/resolve",
            new { action = "Attach", targetGameId = Guid.NewGuid(), flag = "None", update = true });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await AuthClient.JsonAsync(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
        Assert.That(problem.GetProperty("errors").EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "update" }));
    }

    [TestCase("system=sg1000", "system")]
    [TestCase("reason=Failed", "reason")]
    public async Task ListFilter_InvalidValue_Is400WithFieldError(string query, string field)
    {
        using var response = await _admin.GetAsync("/api/review?" + query);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await AuthClient.JsonAsync(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
        Assert.That(problem.GetProperty("errors").TryGetProperty(field, out _), Is.True);
    }

    [Test]
    public async Task ListFilter_QOver100Chars_Is400()
    {
        using var response = await _admin.GetAsync("/api/review?q=" + new string('a', 101));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty("q", out _), Is.True);
    }

    [Test]
    public async Task ListFilter_BySystemReasonAndQ_TotalReflectsFilter()
    {
        var target = await KnownGame();
        var mystery = await Queue();
        var second = await Ambiguous(await Queue(name: "second.gb", bytes: new SyntheticRomBuilder { Seed = 931, Title = "SECOND" }.Build()), target);
        var odd = await Queue(name: "c%_1.gb", bytes: new SyntheticRomBuilder { Seed = 934, Title = "THIRD" }.Build());
        await using (var db = Service<IDbConnectionFactory>().Create())
            await db.ExecuteAsync("UPDATE ingest_review_items SET system_id = 'gbc' WHERE id = @id", new { id = odd.Id });

        async Task<(long Total, long[] Ids)> List(string query)
        {
            using var response = await _admin.GetAsync("/api/review?" + query);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), query);
            var page = await AuthClient.JsonAsync(response);
            return (page.GetProperty("total").GetInt64(),
                page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).Order().ToArray());
        }

        Assert.That(await List("system=gbc"), Is.EqualTo((1L, new[] { odd.Id })));
        Assert.That(await List("reason=AmbiguousGroup"), Is.EqualTo((1L, new[] { second.Id })));
        Assert.That(await List("q=MYST"), Is.EqualTo((1L, new[] { mystery.Id })), "file name, case-insensitive");
        Assert.That(await List("q=tHiRd"), Is.EqualTo((1L, new[] { odd.Id })), "header title, case-insensitive");
        Assert.That(await List("q=%25_"), Is.EqualTo((1L, new[] { odd.Id })), "% and _ match literally");
        Assert.That(await List("system=gb&reason=NoDatMatch"), Is.EqualTo((1L, new[] { mystery.Id })));
        Assert.That(await List("system=gb&reason=NoDatMatch&q=second"), Is.EqualTo((0L, Array.Empty<long>())));
        Assert.That((await List("q=%20%20&pageSize=1")).Total, Is.EqualTo(3), "blank q is ignored; total ignores paging");
    }

    [Test]
    public async Task Search_IsCaseInsensitiveSubstring_WithLimitAndEmptyResults()
    {
        using var found = await _admin.GetAsync("/api/dat/search?system=gb&q=TRI&limit=1");
        var result = await AuthClient.JsonAsync(found);
        Assert.That(result.GetArrayLength(), Is.EqualTo(1));
        Assert.That(result[0].GetProperty("romName").GetString(), Is.EqualTo("Tetris (World).gb"));
        Assert.That(result[0].GetProperty("sizeBytes").GetInt64(), Is.EqualTo(Known.Length));
        Assert.That(result[0].GetProperty("crc32").GetString(), Is.EqualTo(Core.Storage.Crc32.ToHex(Known)));
        using var empty = await _admin.GetAsync("/api/dat/search?system=gbc&q=Tetris");
        Assert.That((await AuthClient.JsonAsync(empty)).GetArrayLength(), Is.Zero);
    }

    [Test]
    public async Task ManualAmbiguous_Is409_DoesNotRequeueOrWrite()
    {
        var item = await Queue();
        var roms = Substitute.For<IRomRepository>();
        roms.ListGameIdsByBaseTitleAsync("gb", "My Game", Arg.Any<CancellationToken>()).Returns(new[] { Guid.NewGuid(), Guid.NewGuid() });
        _pipelineOverride = ActivatorUtilities.CreateInstance<IngestPipeline>(_factory.Services, roms);
        var response = await _admin.ResolveAsync(item.Id, Manual());
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Code, Is.EqualTo("AmbiguousGroup"));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
        Assert.That(File.Exists(item.SourcePath), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnreadableOrNewerTarget_Is409_PreservesReasonAndPending(bool newer)
    {
        var target = await KnownGame();
        var item = await Queue();
        var row = (await Service<IGameRepository>().GetAsync(target, default))!;
        var path = Path.Combine(_factory.Root, "roms", row.ZipPath);
        if (newer)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            var entry = zip.GetEntry("info.json")!;
            JsonNode json;
            using (var stream = entry.Open()) json = JsonNode.Parse(stream)!;
            json["schemaVersion"] = 999;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("info.json").Open());
            writer.Write(json.ToJsonString());
        }
        else File.WriteAllBytes(path, [1, 2, 3]);
        var response = await _admin.ResolveAsync(item.Id, new { action = "Attach", flag = "Hack", targetGameId = target });
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Code, Is.EqualTo(newer ? "InfoJsonNewerSchema" : "TargetZipUnreadable"));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
        Assert.That(File.Exists(item.SourcePath), Is.True);
    }

    [Test]
    public async Task ChangedSource_Is500VerifyFailed_StaysPending()
    {
        var item = await Queue();
        File.WriteAllBytes(item.SourcePath, Known);
        var response = await _admin.ResolveAsync(item.Id, Manual());
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Code, Is.EqualTo("VerifyFailed"));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
    }

    [Test]
    public async Task DbWriteFailed_Is500_StaysPending_SourceAndZipRemain()
    {
        var item = await Queue();
        var mirror = Substitute.For<ILibraryMirror>();
        mirror.ReplaceAsync(Arg.Any<MirrorEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("DB unavailable"));
        _pipelineOverride = ActivatorUtilities.CreateInstance<IngestPipeline>(_factory.Services, mirror);
        var response = await _admin.ResolveAsync(item.Id, Manual());
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Code, Is.EqualTo("DbWriteFailed"));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
        Assert.That(File.Exists(item.SourcePath), Is.True);
        Assert.That(Directory.GetFiles(Path.Combine(_factory.Root, "roms"), "*.zip", SearchOption.AllDirectories), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task LockedSource_Is500IoError_StaysPending()
    {
        var item = await Queue();
        using var locked = new FileStream(item.SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var response = await _admin.ResolveAsync(item.Id, Manual());
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Code, Is.EqualTo("IoError"));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
    }

    [TestCase("GET", "/api/review/999999")]
    [TestCase("POST", "/api/review/999999/resolve")]
    [TestCase("POST", "/api/review/999999/dismiss")]
    public async Task UnknownId_Is404(string method, string url)
    {
        using var response = await _admin.SendAsync(new HttpMethod(method), url, Manual(), true);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task InvalidContext_RejectsDatAndNoneSelections_WithoutChangingRow()
    {
        var target = await KnownGame();
        var item = await Queue();
        foreach (var body in new object[]
        {
            new { action = "Dat", datSystem = "gb", datGameName = "missing" },
            new { action = "Dat", datSystem = "missing", datGameName = "Tetris (World)" },
            new { action = "Attach", flag = "None", targetGameId = target },
        })
        {
            var response = await _admin.ResolveAsync(item.Id, body);
            Assert.That(response.Code, Is.EqualTo("ValidationFailed"));
        }
        await Ambiguous(item, Guid.NewGuid());
        var outside = await _admin.ResolveAsync(item.Id, new { action = "Attach", flag = "None", targetGameId = target });
        Assert.That(outside.Code, Is.EqualTo("ValidationFailed"));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
    }

    [TestCase("Dat")]
    [TestCase("Manual")]
    public async Task GroupedResolution_JoinsExistingGame_AndPreservesMetadata(string action)
    {
        var target = await KnownGame();
        var item = await Queue();
        object body = action == "Dat" ? new { action, datSystem = "gb", datGameName = "Tetris (World)" } : Manual("Tetris");
        var response = await _admin.ResolveAsync(item.Id, body);
        Assert.That(response.Code, Is.Null);
        Assert.That(response.GameId!.Value, Is.EqualTo(target));
        var game = await ReadGame(target);
        Assert.That(game.Roms, Has.Count.EqualTo(2));
        Assert.That(game.ReleaseYear, Is.EqualTo(1989));
        if (action == "Dat") Assert.That(game.Roms.Single(r => r.IsPrimary).Flags, Does.Not.Contain("Unverified"));
    }

    [Test]
    public async Task CorruptExistingRom_Is500VerifyFailed_LeavesOriginalZipAndReviewIntact()
    {
        var target = await KnownGame();
        var item = await Queue();
        var row = (await Service<IGameRepository>().GetAsync(target, default))!;
        var path = Path.Combine(_factory.Root, "roms", row.ZipPath);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("roms/Tetris (World).gb")!;
            entry.Delete();
            using var stream = zip.CreateEntry("roms/Tetris (World).gb").Open();
            stream.Write(Unknown);
        }
        var before = File.ReadAllBytes(path);
        var response = await _admin.ResolveAsync(item.Id, new { action = "Attach", targetGameId = target, flag = "Hack" });
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Code, Is.EqualTo("VerifyFailed"));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
        Assert.That(File.Exists(item.SourcePath), Is.True);
    }

    [Test]
    public async Task InvalidBody_ReportsFieldErrors_AndDoesNotResolve()
    {
        var item = await Queue();
        var response = await _admin.ResolveAsync(item.Id, new { action = "Manual", title = "   " });
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Code, Is.EqualTo("ValidationFailed"));
        Assert.That(response.Errors!.Value.TryGetProperty("title", out _), Is.True);
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
    }

    [Test]
    public async Task DatSelection_UsesSelectedFamilySystemAndItsFirstExtension()
    {
        var item = await Queue();
        var response = await _admin.ResolveAsync(item.Id, new { action = "Dat", datSystem = "gbc", datGameName = "Color Game (World)" });
        Assert.That(response.Code, Is.Null);
        var game = await ReadGame(response.GameId!.Value);
        Assert.That(game.System, Is.EqualTo("gbc"));
        Assert.That(game.Roms.Single().FileName, Is.EqualTo("Color Game (World) (Unverified).gbc"));
    }

    [Test]
    public async Task AttachToAnotherSystem_Is400_StaysPending()
    {
        var target = (await Ingest(Drop("color.gbc", Color)))!.GameId!.Value;
        var item = await Queue();
        var response = await _admin.ResolveAsync(item.Id, new { action = "Attach", targetGameId = target, flag = "Hack" });
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Code, Is.EqualTo("ValidationFailed"));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
    }

    [Test]
    public async Task Search_DefaultsToTwenty_AndHonorsSmallerLimits()
    {
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
            [Gb] = Enumerable.Range(1, 25).Select(i => new FixtureGame($"Demo {i} (World)", $"Demo {i} (World).gb", new SyntheticRomBuilder { Seed = 1000 + i }.Build())).ToArray(),
            ["Nintendo - Game Boy Color"] = [],
        }, titleDb: IngestSeeder.EmptySwitch, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
        using var defaults = await _admin.GetAsync("/api/dat/search?system=gb&q=demo");
        Assert.That((await AuthClient.JsonAsync(defaults)).GetArrayLength(), Is.EqualTo(20));
        using var smaller = await _admin.GetAsync("/api/dat/search?system=gb&q=demo&limit=3");
        Assert.That((await AuthClient.JsonAsync(smaller)).GetArrayLength(), Is.EqualTo(3));
    }

    [Test]
    public async Task Search_ListsARepeatedGameNameOnce_BeforeTheLimit()
    {
        // B15: Redump repeats a game once per serial, with the same name.
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
            [Gb] =
            [
                new FixtureGame("Repeat (World)", "Repeat (World).gb", new SyntheticRomBuilder { Seed = 2001 }.Build()),
                new FixtureGame("Repeat (World)", "Repeat (World) (Alt).gb", new SyntheticRomBuilder { Seed = 2002 }.Build()),
                new FixtureGame("Repeat Two (World)", "Repeat Two (World).gb", new SyntheticRomBuilder { Seed = 2003 }.Build()),
            ],
            ["Nintendo - Game Boy Color"] = [],
        }, titleDb: IngestSeeder.EmptySwitch, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();

        using var response = await _admin.GetAsync("/api/dat/search?system=gb&q=repeat&limit=2");

        var hits = (await AuthClient.JsonAsync(response)).EnumerateArray().Select(h => (h.GetProperty("gameName").GetString(), h.GetProperty("romName").GetString())).ToList();
        Assert.That(hits, Is.EqualTo(new[] { ("Repeat (World)", "Repeat (World).gb"), ("Repeat Two (World)", "Repeat Two (World).gb") }));
    }

    [Test]
    public async Task ListFilter_AmbiguousSet_IsAccepted()
    {
        using var response = await _admin.GetAsync("/api/review?reason=AmbiguousSet");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("total").GetInt64(), Is.Zero);
    }

    [Test]
    public async Task AlreadyResolvedItem_CannotBeResolvedOrDismissedAgain()
    {
        var item = await Queue();
        var first = await _admin.ResolveAsync(item.Id, Manual());
        Assert.That(first.Code, Is.Null);
        foreach (var action in new[] { "resolve", "dismiss" })
        {
            using var again = await _admin.PostAsync($"/api/review/{item.Id}/{action}", action == "resolve" ? Manual() : null);
            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Resolved"));
    }

    [Test]
    public async Task DismissAfterClassificationRename_PreservesActualSourceExtension()
    {
        var item = await Queue(bytes: new SyntheticRomBuilder { Seed = 933, CgbFlag = 0xC0 }.Build());
        Assert.That(item.SourcePath, Does.EndWith("mystery.gbc"));
        using var response = await _admin.PostAsync($"/api/review/{item.Id}/dismiss");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(File.Exists(Path.Combine(_factory.Root, "ingest", "gb", "_failed", "mystery.gbc")), Is.True);
    }

    [Test]
    public async Task InvalidGeneratedEntryName_IsValidationFailed_StaysPending()
    {
        var item = await Queue();
        var response = await _admin.ResolveAsync(item.Id, Manual("My..Game"));
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Code, Is.EqualTo("ValidationFailed"));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
    }

    [Test]
    public async Task Resolve_Is202WithAJobId_AndTheJobIsAReviewResolve_B1()
    {
        var item = await Queue();

        using var response = await _admin.PostAsync($"/api/review/{item.Id}/resolve", Manual());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var jobId = (await AuthClient.JsonAsync(response)).GetProperty("jobId").GetGuid();
        await WaitIdleAsync();
        var job = (await Service<IJobRepository>().GetAsync(jobId, default))!;
        Assert.That((job.Type, job.Mode, job.Status, job.RequestedBy), Is.EqualTo((JobType.ReviewResolve, (JobMode?)null, JobStatus.Completed, (int?)_adminId)));
        var summary = JsonNode.Parse(job.Summary!)!;
        Assert.That((summary["reviewId"]!.GetValue<long>(), summary["fileName"]!.GetValue<string>()), Is.EqualTo((item.Id, "mystery.gb")));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Resolved"));
    }

    [Test]
    public async Task Resolve_WhileAnotherJobRuns_Is409JobRunning_B1()
    {
        var item = await Queue();
        var other = new NoopTestJob();
        var started = await Service<JobRunner>().StartAsync(other, JobTrigger.Manual, null, default);
        await other.Started.Task;

        var result = await _admin.ResolveAsync(item.Id, Manual());

        other.Release.SetResult();
        await started!.Completion;
        Assert.That((result.Status, result.Code), Is.EqualTo((HttpStatusCode.Conflict, "JobRunning")));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
    }

    [Test]
    public async Task Resolve_WithABadDisc_Is400OnDisc_B9()
    {
        var item = await Queue();

        var result = await _admin.ResolveAsync(item.Id, new { action = "Manual", title = "My Game", regions = Array.Empty<string>(),
            languages = Array.Empty<string>(), flags = Array.Empty<string>(), disc = 0 });

        Assert.That((result.Status, result.Code), Is.EqualTo((HttpStatusCode.BadRequest, "ValidationFailed")));
        Assert.That(result.Errors!.Value.TryGetProperty("disc", out _), Is.True);
    }

    [Test]
    public async Task Resolve_OnAGbItemWithADisc_FailsInTheJobOnDisc_B9()
    {
        var item = await Queue();

        var result = await _admin.ResolveAsync(item.Id, new { action = "Manual", title = "My Game", regions = Array.Empty<string>(),
            languages = Array.Empty<string>(), flags = Array.Empty<string>(), disc = 2 });

        Assert.That((result.Status, result.Code), Is.EqualTo((HttpStatusCode.Accepted, "ValidationFailed")));
        Assert.That(result.Errors!.Value.GetProperty("disc")[0].GetString(), Is.EqualTo("Disc numbers are for disc systems."));
    }

    private async Task WaitIdleAsync()
    {
        for (var i = 0; i < 600 && Service<JobRunner>().Current is not null; i++)
            await Task.Delay(50);
    }
}
