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
using RnRomManager.Core.Storage;
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
    /// <summary>The DS DAT's one game; Review items are other dumps that resolve to it or suggest it.</summary>
    private static readonly byte[] StylusQuest = new SyntheticNdsRomBuilder { Seed = 940, Title = "STYLUS QUEST", GameCode = "ASQE" }.Build();
    private static readonly byte[] DsUnknown = new SyntheticNdsRomBuilder { Seed = 941, Title = "STYLUS QUEST", GameCode = "ASQP" }.Build();
    private static readonly byte[] DsOther = new SyntheticNdsRomBuilder { Seed = 942, Title = "PEN PALS", GameCode = "APPE" }.Build();
    private static readonly byte[] DsBadCrc = new SyntheticNdsRomBuilder { Seed = 943, Title = "BROKEN", BadHeaderCrc = true }.Build();
    /// <summary>CHD B3: the Saturn DAT's data track a <c>.chd</c> is DAT-picked against (the <c>.chd</c> itself never matches).</summary>
    private static readonly byte[] ChdQuestTrack = new SyntheticDiscBuilder { Seed = 2401 }.Build();
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
            TestServices = services =>
            {
                services.AddTransient(sp => ActivatorUtilities.CreateInstance<ReviewResolver>(sp, _pipelineOverride ?? sp.GetRequiredService<IngestPipeline>()));
                RnRomManager.Core.Systems.SystemHandlerRegistration.AddSystemHandler<LegacyExternalTestHandler>(services);
            },
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
            [IngestSeeder.SaturnDat] = [new FixtureGame("Chd Quest (Japan)", "Chd Quest (Japan) (Track 1).bin", ChdQuestTrack)],
            [IngestSeeder.SegaCdDat] = [],
            [IngestSeeder.ThreeDoDat] = [],
            [IngestSeeder.C64Dat] = [],
            [IngestSeeder.PetDat] = [],
            [IngestSeeder.Vic20Dat] = [], [IngestSeeder.SatellaviewDat] = [], [IngestSeeder.SufamiTurboDat] = [], [IngestSeeder.FdsDat] = [],
            [IngestSeeder.AmigaDat] = [], [IngestSeeder.ThreeDsDat] = [], [IngestSeeder.TurboGrafxCdDat] = [],
            [IngestSeeder.NintendoDsDat] = [new FixtureGame("Stylus Quest (USA)", "Stylus Quest (USA).nds", StylusQuest) { ReleaseYear = 2006 }],
            [Gb] = [new FixtureGame("Tetris (World)", "Tetris (World).gb", Known)
            { ReleaseYear = 1989, Developers = ["Nintendo"], Publishers = ["Nintendo"], Genres = ["Puzzle"] }],
            [LegacyExternalTestHandler.DatNameValue] = [],
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

    [Test]
    public async Task AttachRelease_TagsFromTheFileName_Unverified_NotPrimary_A2()
    {
        var target = await KnownGame();
        var item = await Queue(false, "mystery (World) (Rev 1).gb");
        var response = await _admin.ResolveAsync(item.Id, new { action = "Attach", targetGameId = target, flag = "Release" });
        Assert.That(response.Code, Is.Null);
        var game = await ReadGame(target);
        var rom = game.Roms.Single(r => r.Metadata.Sha1 == item.Sha1);
        Assert.That((rom.BaseTitle, rom.FileName, rom.Revision),
            Is.EqualTo(("Tetris", "mystery (World) (Rev 1) (Unverified).gb", "Rev 1")));
        Assert.That(rom.Regions, Is.EqualTo(new[] { "World" }));
        Assert.That(rom.Flags, Is.EqualTo(new[] { "Unverified" }));
        Assert.That(rom.IsPrimary, Is.False);
        Assert.That(rom.Identification.Method, Is.EqualTo(IdentificationMethod.Manual));
        Assert.That(game.Roms.Single(r => r.IsPrimary).Identification.Method, Is.EqualTo(IdentificationMethod.Dat));
    }

    [Test]
    public async Task Attach_ToALegacyGame_IsStorageMigrationPending_AndStaysPending()
    {
        var legacyRom = new SyntheticRomBuilder { Seed = 950 }.Build();
        var game = TestLibrary.Game("Old Ds", TestLibrary.Rom("Old Ds (USA).nds", legacyRom)) with { System = LegacyExternalTestHandler.SystemId };
        var zip = Path.Combine(_factory.Root, "roms", "legacy", "O", "Old Ds.zip");
        LegacyZipWriter.Write(zip, game, legacyRom);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, "legacy/O/Old Ds.zip", new FileInfo(zip).Length, DateTime.UtcNow), default);
        var zipBytes = File.ReadAllBytes(zip);
        var bytes = new SyntheticRomBuilder { Seed = 951 }.Build();
        var path = Path.Combine(_factory.Root, "ingest", LegacyExternalTestHandler.SystemId, "hack.nds");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var queued = await Ingest(new IngestFile(LegacyExternalTestHandler.SystemId, path, "hack.nds", bytes.Length, File.GetLastWriteTimeUtc(path)));
        Assert.That(queued!.Outcome, Is.EqualTo(IngestOutcome.Review), queued.Details);
        var item = (await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!;

        var response = await _admin.ResolveAsync(item.Id, new { action = "Attach", targetGameId = game.GameId, flag = "Hack" });

        Assert.That(response.Code, Is.EqualTo(IngestErrors.StorageMigrationPending));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
        Assert.That(File.Exists(path), Is.True);
        Assert.That(File.ReadAllBytes(zip), Is.EqualTo(zipBytes));
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
    public async Task DeleteBatch_DeletesEachInOrder_UnknownIsSkipped()
    {
        var first = await Queue();
        var second = await Queue(name: "other.gb", bytes: new SyntheticRomBuilder { Seed = 931, Title = "OTHER" }.Build());

        using var response = await _admin.PostAsync("/api/review/delete", new { ids = new[] { first.Id, 999999, second.Id } });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(response);
        Assert.That((body.GetProperty("deleted").GetInt32(), body.GetProperty("skipped").GetInt32(), body.GetProperty("failed").GetArrayLength()),
            Is.EqualTo((2, 1, 0)));
        Assert.That(File.Exists(first.SourcePath) || File.Exists(second.SourcePath), Is.False);
        Assert.That(await Reviews.GetAsync(first.Id, default), Is.Null);
        Assert.That(await Reviews.GetAsync(second.Id, default), Is.Null);
        await Service<RnRomManager.Api.AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM audit_events WHERE action = 'ReviewDeleted'"), Is.EqualTo(2));
    }

    [TestCase("[]")]
    [TestCase("[0]")]
    [TestCase("[-3]")]
    [TestCase("[5,5]")]
    [TestCase("null")]
    [TestCase("tooMany")]
    public async Task DeleteBatch_InvalidIds_Is400(string ids)
    {
        object body = ids == "tooMany"
            ? new { ids = Enumerable.Range(1, 501).ToArray() }
            : System.Text.Json.JsonSerializer.Deserialize<object>($$"""{"ids":{{ids}}}""")!;

        using var response = await _admin.PostAsync("/api/review/delete", body);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty("ids", out _), Is.True);
    }

    [Test]
    public async Task DeleteBatch_LockBusy_Is409_AndDeletesNothing()
    {
        var item = await Queue();
        using (Service<JobLock>().TryAcquire())
        {
            using var busy = await _admin.PostAsync("/api/review/delete", new { ids = new[] { item.Id } });
            Assert.That(busy.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(busy), Is.EqualTo("JobRunning"));
        }
        Assert.That(File.Exists(item.SourcePath), Is.True);
        Assert.That(await Reviews.GetAsync(item.Id, default), Is.Not.Null);
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
    [TestCase("POST", "/api/review/delete")]
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
            "SELECT action, user_id, username, job_id, path, details FROM audit_events WHERE action NOT IN ('SignedIn', 'SignInFailed', 'LockedOut', 'PasswordChanged') ORDER BY id")).ToList();
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

    private IngestFile DropDs(string name, byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", "nds", name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return new IngestFile("nds", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
    }

    [Test]
    public async Task SegaCd_UnmatchedIso_WaitsInReview_AndManualStoresItAsIsoNextToTheZip()
    {
        var bytes = new SyntheticDiscBuilder { Seed = 2201 }.Build();
        var path = Path.Combine(_factory.Root, "ingest", "segacd", "mystery.iso");
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        var result = await Ingest(new IngestFile("segacd", path, "mystery.iso", bytes.Length, File.GetLastWriteTimeUtc(path)));

        Assert.That((result!.Outcome, result.Reason, result.SystemId), Is.EqualTo((IngestOutcome.Review, "NoDatMatch", "segacd")));
        var item = (await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!;
        Assert.That(item.SystemId, Is.EqualTo("segacd"));

        var response = await _admin.ResolveAsync(item.Id, new { action = "Manual", title = "Mystery Disc", regions = new[] { "USA" }, languages = Array.Empty<string>(), flags = Array.Empty<string>() });

        Assert.That(response.Code, Is.Null);
        var game = await ReadGame(response.GameId!.Value);
        var rom = game.Roms.Single();
        Assert.That((game.System, rom.FileName, rom.ExternalFileName, rom.Identification.Method),
            Is.EqualTo(("segacd", "Mystery Disc (USA).iso", "Mystery Disc (USA).iso", IdentificationMethod.Manual)));
        Assert.That(File.ReadAllBytes(Path.Combine(_factory.Root, "roms", "segacd", "M", "Mystery Disc (USA).iso")), Is.EqualTo(bytes));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Resolved"));
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public async Task ThreeDo_UnmatchedIso_WaitsInReview_AndManualStoresItAsIsoNextToTheZip()
    {
        var bytes = new SyntheticDiscBuilder { Seed = 2511 }.Build();
        var path = Path.Combine(_factory.Root, "ingest", "3do", "mystery.iso");
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        var result = await Ingest(new IngestFile("3do", path, "mystery.iso", bytes.Length, File.GetLastWriteTimeUtc(path)));

        Assert.That((result!.Outcome, result.Reason, result.SystemId), Is.EqualTo((IngestOutcome.Review, "NoDatMatch", "3do")));
        var item = (await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!;
        Assert.That(item.SystemId, Is.EqualTo("3do"));

        var response = await _admin.ResolveAsync(item.Id, new { action = "Manual", title = "Mystery Disc", regions = new[] { "USA" }, languages = Array.Empty<string>(), flags = Array.Empty<string>() });

        Assert.That(response.Code, Is.Null);
        var game = await ReadGame(response.GameId!.Value);
        var rom = game.Roms.Single();
        Assert.That((game.System, rom.FileName, rom.ExternalFileName, rom.Identification.Method),
            Is.EqualTo(("3do", "Mystery Disc (USA).iso", "Mystery Disc (USA).iso", IdentificationMethod.Manual)));
        Assert.That(File.ReadAllBytes(Path.Combine(_factory.Root, "roms", "3do", "M", "Mystery Disc (USA).iso")), Is.EqualTo(bytes));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Resolved"));
        Assert.That(File.Exists(path), Is.False);
    }

    private async Task<ReviewItem> QueueDs(string name, byte[] bytes, bool archive = false)
    {
        if (archive)
        {
            var path = Path.Combine(_factory.Root, "ingest", "nds", "ds-pack.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var entry = zip.CreateEntry("nested/" + name).Open())
                    entry.Write(bytes);
                using (var notes = zip.CreateEntry("notes.txt").Open())
                    notes.Write("companion"u8);
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
            await RunIngest();
        }
        else
        {
            var result = await Ingest(DropDs(name, bytes));
            Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Review, "NoDatMatch")));
        }
        var item = (await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!;
        Assert.That((item.SystemId, item.Reason), Is.EqualTo(("nds", "NoDatMatch")));
        return item;
    }

    /// <summary>S1: the file next to the ZIP, <c>info.json</c> and the source agree, and the ZIP holds no ROM entry.</summary>
    private async Task AssertStoredExternal(Guid gameId, string sha1, byte[] source)
    {
        var row = (await Service<IGameRepository>().GetAsync(gameId, default))!;
        var zipPath = Path.GetFullPath(Path.Combine(_factory.Root, "roms", row.ZipPath));
        using var zip = LibraryZip.Open(zipPath, Service<InfoJsonReader>());
        var rom = zip.InfoJson!.Game!.Roms.Single(r => r.Metadata.Sha1 == sha1);
        Assert.That(rom.ExternalFileName, Is.Not.Null);
        Assert.That(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(zipPath)!, rom.ExternalFileName!)), Is.EqualTo(source));
        Assert.That(zip.EntryNames.Where(n => n.StartsWith("roms/", StringComparison.Ordinal)), Is.Empty);
        Assert.That((rom.Metadata.SizeBytes, rom.Metadata.Crc32, rom.Metadata.Md5, rom.Metadata.Sha1),
            Is.EqualTo(((long)source.Length, Crc32.ToHex(source), Convert.ToHexStringLower(MD5.HashData(source)), Convert.ToHexStringLower(SHA1.HashData(source)))));
    }

    [Test]
    public async Task Ds_UnmatchedHeaders_AValidCrcGoesToReview_ABadCrcIsInvalidHeader()
    {
        var valid = await Ingest(DropDs("other.nds", DsOther));
        var invalid = await Ingest(DropDs("broken.nds", DsBadCrc));

        Assert.That((valid!.Outcome, valid.Reason), Is.EqualTo((IngestOutcome.Review, "NoDatMatch")));
        Assert.That((invalid!.Outcome, invalid.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.InvalidHeader)));
        var item = (await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(DsOther)), default))!;
        Assert.That(JsonNode.Parse(item.Header!)!["title"]!.GetValue<string>(), Is.EqualTo("PEN PALS"));
        Assert.That(await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(DsBadCrc)), default), Is.Null);
    }

    [Test]
    public async Task Ds_HeaderTitle_SuggestsTheDatGame()
    {
        var item = await QueueDs("quest-eu.nds", DsUnknown);

        Assert.That(item.Suggestions, Does.Contain("Stylus Quest"));
    }

    [Test]
    public async Task Ds_ManualResolution_OfALooseItem_StoresTheSourceBytesNextToTheZip()
    {
        var item = await QueueDs("pen pals.nds", DsOther);

        var response = await _admin.ResolveAsync(item.Id, new { action = "Manual", title = "Pen Pals", regions = new[] { "USA" }, languages = Array.Empty<string>(), flags = Array.Empty<string>() });

        Assert.That(response.Code, Is.Null);
        var game = await ReadGame(response.GameId!.Value);
        Assert.That((game.System, game.Roms.Single().FileName), Is.EqualTo(("nds", "Pen Pals (USA).nds")));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Resolved"));
        Assert.That(File.Exists(item.SourcePath), Is.False);
        await AssertStoredExternal(response.GameId.Value, item.Sha1, DsOther);
    }

    [Test]
    public async Task Ds_DatSelection_OfAnArchiveItem_StoresTheEntryBytesNextToTheZip()
    {
        var item = await QueueDs("quest-eu.nds", DsUnknown, archive: true);
        Assert.That((item.ArchivePath, item.ArchiveEntry), Is.EqualTo((Path.Combine(_factory.Root, "ingest", "nds", "ds-pack.zip"), "nested/quest-eu.nds")));

        var response = await _admin.ResolveAsync(item.Id, new { action = "Dat", datSystem = "nds", datGameName = "Stylus Quest (USA)" });

        Assert.That(response.Code, Is.Null);
        var game = await ReadGame(response.GameId!.Value);
        Assert.That((game.Title, game.ReleaseYear), Is.EqualTo(("Stylus Quest", (int?)2006)));
        Assert.That(game.Roms.Single().FileName, Is.EqualTo("Stylus Quest (USA) (Unverified).nds"));
        await AssertStoredExternal(response.GameId.Value, item.Sha1, DsUnknown);
    }

    /// <summary>ROM Zip Config B3, B13: an item queued while DS was beside the ZIP resolves into the ZIP after the toggle.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Ds_ItemQueuedBeforeTheEmbedToggle_ResolvesIntoTheZip(bool archive)
    {
        var item = await QueueDs("pen pals.nds", DsOther, archive);
        await Service<IRomStorageSettings>().SetAsync("nds", true, default);

        var response = await _admin.ResolveAsync(item.Id, new { action = "Manual", title = "Pen Pals", regions = new[] { "USA" }, languages = Array.Empty<string>(), flags = Array.Empty<string>() });

        Assert.That(response.Code, Is.Null);
        var row = (await Service<IGameRepository>().GetAsync(response.GameId!.Value, default))!;
        var zipPath = Path.GetFullPath(Path.Combine(_factory.Root, "roms", row.ZipPath));
        using (var zip = LibraryZip.Open(zipPath, Service<InfoJsonReader>()))
        {
            var rom = zip.InfoJson!.Game!.Roms.Single();
            Assert.That(rom.ExternalFileName, Is.Null);
            Assert.That(zip.EntryNames, Does.Contain(LibraryZip.RomEntry(rom.FileName)));
        }
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(zipPath)!), Is.EqualTo(new[] { zipPath }), "nothing beside the ZIP");
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Resolved"));
        Assert.That(File.Exists(item.ArchivePath ?? item.SourcePath), Is.EqualTo(archive), "an embedded system keeps a resolved archive until the next ingest");
        if (archive)
        {
            await RunIngest();
            Assert.That(File.Exists(item.ArchivePath), Is.False);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Ds_ItemQueuedBeforeTheEmbedToggle_IsDeleted(bool archive)
    {
        var item = await QueueDs("pen pals.nds", DsOther, archive);
        await Service<IRomStorageSettings>().SetAsync("nds", true, default);

        using var response = await _admin.SendAsync(HttpMethod.Delete, $"/api/review/{item.Id}", null, true);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(File.Exists(item.ArchivePath ?? item.SourcePath), Is.False);
        Assert.That(await Reviews.GetAsync(item.Id, default), Is.Null);
    }

    /// <summary>
    /// B5 (T9 retry): an archived DS item queued beside the ZIP whose SHA-1 a DS game inside its ZIP already holds is a
    /// Duplicate; the owner row's own layout decides. (An embedded-mode resolve never reaches this check: see § Decisions.)
    /// </summary>
    [Test]
    public async Task Ds_ArchivedItem_WithAnOwnerInsideItsZip_IsDuplicate()
    {
        var item = await QueueDs("quest-eu.nds", DsUnknown, archive: true);
        var owner = TestLibrary.Game("Quest", TestLibrary.Rom("Quest (Europe).nds", DsUnknown)) with { System = "nds" };
        var zip = Path.Combine(_factory.Root, "roms", "nds", "Q", "Quest.zip");
        LegacyZipWriter.Write(zip, owner, DsUnknown); // the retry syncs the library first, so the owner's ZIP must exist
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(owner, "nds/Q/Quest.zip", new FileInfo(zip).Length, DateTime.UtcNow), default);

        var response = await _admin.ResolveAsync(item.Id, new { action = "Manual", title = "Quest 2", regions = new[] { "USA" }, languages = Array.Empty<string>(), flags = Array.Empty<string>() });

        Assert.That(response.Code, Is.EqualTo("Duplicate"));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
    }

    [Test]
    public async Task Ds_SameLengthChangedSource_IsVerifyFailed_AndStaysPending()
    {
        var item = await QueueDs("pen pals.nds", DsOther);
        var changed = DsOther.ToArray();
        changed[^1] ^= 0xFF;
        File.WriteAllBytes(item.SourcePath, changed);

        var response = await _admin.ResolveAsync(item.Id, new { action = "Manual", title = "Pen Pals", regions = Array.Empty<string>(), languages = Array.Empty<string>(), flags = Array.Empty<string>() });

        Assert.That(response.Code, Is.EqualTo("VerifyFailed"));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
        Assert.That(File.ReadAllBytes(item.SourcePath), Is.EqualTo(changed), "the source is left where it is");
    }

    [Test]
    public async Task Ds_ASignedInUserWithoutAdmin_Gets403_ForImportAndResolution()
    {
        var item = await QueueDs("pen pals.nds", DsOther);
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "ds-user");
        (await user.LoginAsync("ds-user", TestUsers.Password)).Dispose();

        using var import = await user.PostAsync("/api/jobs/ingest", new { files = new[] { new { system = "nds", name = "pen pals.nds" } } });
        using var resolve = await user.PostAsync($"/api/review/{item.Id}/resolve", Manual());

        Assert.That((import.StatusCode, resolve.StatusCode), Is.EqualTo((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Pending"));
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
            [IngestSeeder.AmigaDat] = [], [IngestSeeder.ThreeDsDat] = [], [IngestSeeder.TurboGrafxCdDat] = [],
            [IngestSeeder.NintendoDsDat] = [],
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
            [IngestSeeder.AmigaDat] = [], [IngestSeeder.ThreeDsDat] = [], [IngestSeeder.TurboGrafxCdDat] = [],
            [IngestSeeder.NintendoDsDat] = [],
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

    /// <summary>Release editing B1: a generated name holding <c>..</c> is valid on every system (a name that is exactly <c>..</c> is refused; ReviewResolverTests).</summary>
    [Test]
    public async Task GeneratedEntryNameWithDotsInside_Resolves_B1()
    {
        var item = await Queue();
        var response = await _admin.ResolveAsync(item.Id, Manual("My..Game"));
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Code, Is.Null);
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Resolved"));
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

    [Test]
    public async Task Detail_OfAnUnmatchedSetMember_ListsTheSet_AndIsNotDismissible_B13()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_factory.Root, "ingest", "psx")).FullName;
        var cueText = "FILE \"Disc (Track 1).bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
            + "FILE \"Disc (Track 2).bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n";
        foreach (var (name, bytes) in new[] { ("Disc.cue", System.Text.Encoding.UTF8.GetBytes(cueText)),
            ("Disc (Track 1).bin", RandomNumberGenerator.GetBytes(2352)), ("Disc (Track 2).bin", RandomNumberGenerator.GetBytes(2352)) })
        {
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            File.SetLastWriteTimeUtc(Path.Combine(folder, name), DateTime.UtcNow.AddMinutes(-5));
        }
        await RunIngest();
        var rows = (await Reviews.ListAsync("Pending", 1, 50, default)).Items.Where(r => r.SystemId == "psx").ToList();
        var bin = rows.Single(r => r.OriginalFileName == "Disc (Track 2).bin");

        using (var detail = await _admin.GetAsync($"/api/review/{bin.Id}"))
        {
            var json = await AuthClient.JsonAsync(detail);
            Assert.That((json.GetProperty("dismissible").GetBoolean(), json.GetProperty("fileSets").GetBoolean(), json.GetProperty("resolvable").GetBoolean()),
                Is.EqualTo((false, true, true)));
            Assert.That(json.GetProperty("setMembers").EnumerateArray().Select(m => (m.GetProperty("fileName").GetString(), m.GetProperty("sizeBytes").GetInt64())),
                Is.EqualTo(new[] { ("Disc.cue", (long)cueText.Length), ("Disc (Track 1).bin", 2352L), ("Disc (Track 2).bin", 2352L) }));
            Assert.That(json.GetProperty("setMembers")[0].GetProperty("reviewId").GetInt64(), Is.EqualTo(rows.Single(r => r.OriginalFileName == "Disc.cue").Id));
        }
        using (var dismiss = await _admin.PostAsync($"/api/review/{bin.Id}/dismiss"))
        {
            Assert.That(dismiss.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(dismiss), Is.EqualTo(ReviewResolver.SetFileNotResolvable));
        }
    }

    [Test]
    public async Task Detail_OfACloneCdCcd_ListsTheSet_AndItsImgCantBeDismissed_CloneCdB7()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_factory.Root, "ingest", "psx")).FullName;
        var ccd = System.Text.Encoding.UTF8.GetBytes("[Disc]\nSessions=1\n[TRACK 1]\nMODE=2\nINDEX 1=0\n");
        foreach (var (name, bytes) in new[] { ("Clone.ccd", ccd), ("Clone.img", RandomNumberGenerator.GetBytes(2352)), ("Clone.sub", RandomNumberGenerator.GetBytes(96)) })
        {
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            File.SetLastWriteTimeUtc(Path.Combine(folder, name), DateTime.UtcNow.AddMinutes(-5));
        }
        await RunIngest();
        var rows = (await Reviews.ListAsync("Pending", 1, 50, default)).Items.Where(r => r.SystemId == "psx").ToList();

        using (var detail = await _admin.GetAsync($"/api/review/{rows.Single(r => r.OriginalFileName == "Clone.ccd").Id}"))
        {
            var json = await AuthClient.JsonAsync(detail);
            Assert.That((json.GetProperty("resolvable").GetBoolean(), json.GetProperty("dismissible").GetBoolean()), Is.EqualTo((true, false)));
            Assert.That(json.GetProperty("setMembers").EnumerateArray().Select(m => m.GetProperty("fileName").GetString()),
                Is.EqualTo(new[] { "Clone.ccd", "Clone.img", "Clone.sub" }));
        }
        using (var dismiss = await _admin.PostAsync($"/api/review/{rows.Single(r => r.OriginalFileName == "Clone.img").Id}/dismiss"))
        {
            Assert.That(dismiss.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(dismiss), Is.EqualTo(ReviewResolver.SetFileNotResolvable));
        }
    }

    /// <summary>File type gaps D10: drops <paramref name="files"/> in the PlayStation ingest folder, ingests, and returns the pending psx rows.</summary>
    private async Task<List<ReviewItem>> DropPsxSetAsync(params (string Name, byte[] Bytes)[] files)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_factory.Root, "ingest", "psx")).FullName;
        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
            File.SetLastWriteTimeUtc(Path.Combine(folder, name), DateTime.UtcNow.AddMinutes(-5));
        }
        await RunIngest();
        return (await Reviews.ListAsync("Pending", 1, 50, default)).Items.Where(r => r.SystemId == "psx").ToList();
    }

    [Test]
    public async Task Detail_OfATocSetsToc_IsResolvable_NotDismissible_AndListsTheSet_D10()
    {
        var toc = System.Text.Encoding.UTF8.GetBytes("TRACK MODE2_RAW\nFILE \"Toc (Track 1).bin\" 0 00:00:01\nTRACK AUDIO\nFILE \"Toc (Track 2).bin\" 0\n");
        var rows = await DropPsxSetAsync(("Toc.toc", toc), ("Toc (Track 1).bin", RandomNumberGenerator.GetBytes(2352)),
            ("Toc (Track 2).bin", RandomNumberGenerator.GetBytes(2352)));

        using var detail = await _admin.GetAsync($"/api/review/{rows.Single(r => r.OriginalFileName == "Toc.toc").Id}");
        var json = await AuthClient.JsonAsync(detail);
        Assert.That((json.GetProperty("resolvable").GetBoolean(), json.GetProperty("dismissible").GetBoolean()), Is.EqualTo((true, false)));
        Assert.That(json.GetProperty("setMembers").EnumerateArray().Select(m => m.GetProperty("fileName").GetString()),
            Is.EqualTo(new[] { "Toc.toc", "Toc (Track 1).bin", "Toc (Track 2).bin" }));
    }

    [Test]
    public async Task Dismiss_OfAnMdsSetsMdf_IsSetFileNotResolvable_D10()
    {
        var rows = await DropPsxSetAsync(("Mds.mds", SyntheticMdsBuilder.OneTrack()), ("Mds.mdf", RandomNumberGenerator.GetBytes(2352)));

        using var dismiss = await _admin.PostAsync($"/api/review/{rows.Single(r => r.OriginalFileName == "Mds.mdf").Id}/dismiss");
        Assert.That(dismiss.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(dismiss), Is.EqualTo(ReviewResolver.SetFileNotResolvable));
    }

    /// <summary>CHD B3: synthetic bytes standing in for a CHD (never a real image).</summary>
    private static byte[] FakeChd(int seed)
    {
        var bytes = new SyntheticDiscBuilder { Seed = seed }.Build();
        "MComprHD"u8.CopyTo(bytes);
        return bytes;
    }

    private string DropDisc(string system, string name, byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", system, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }

    /// <summary>CHD B2: a lone <c>.chd</c> queued as <c>NoDatMatch</c> on <paramref name="system"/>.</summary>
    private async Task<ReviewItem> QueueChd(string system, string name, int seed)
    {
        var bytes = FakeChd(seed);
        var path = DropDisc(system, name, bytes);
        var result = await Ingest(new IngestFile(system, path, name, bytes.Length, File.GetLastWriteTimeUtc(path)));
        Assert.That((result!.Outcome, result.Reason, result.SystemId), Is.EqualTo((IngestOutcome.Review, "NoDatMatch", system)));
        return (await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!;
    }

    [Test]
    public async Task Chd_Manual_IsStoredNextToTheZip_UnderTheBuiltName()
    {
        var item = await QueueChd("segacd", "sonic.chd", 2402);

        var response = await _admin.ResolveAsync(item.Id, new { action = "Manual", title = "Sonic CD", regions = new[] { "USA" }, languages = Array.Empty<string>(), flags = Array.Empty<string>() });

        Assert.That(response.Code, Is.Null);
        var rom = (await ReadGame(response.GameId!.Value)).Roms.Single();
        Assert.That((rom.FileName, rom.ExternalFileName, rom.Identification.Method),
            Is.EqualTo(("Sonic CD (USA).chd", "Sonic CD (USA).chd", IdentificationMethod.Manual)));
        Assert.That(File.Exists(Path.Combine(_factory.Root, "roms", "segacd", "S", "Sonic CD (USA).chd")), Is.True);
        Assert.That(File.Exists(item.SourcePath), Is.False);
    }

    [Test]
    public async Task TurboGrafxCd_UnmatchedChd_WaitsInReview_AndManualStoresItAsChdNextToTheZip()
    {
        var item = await QueueChd("tgcd", "mystery.chd", 2605);
        Assert.That(item.SystemId, Is.EqualTo("tgcd"));

        var response = await _admin.ResolveAsync(item.Id, new { action = "Manual", title = "Mystery Disc", regions = new[] { "Japan" }, languages = Array.Empty<string>(), flags = Array.Empty<string>() });

        Assert.That(response.Code, Is.Null);
        var game = await ReadGame(response.GameId!.Value);
        var rom = game.Roms.Single();
        Assert.That((game.System, rom.FileName, rom.ExternalFileName, rom.Identification.Method),
            Is.EqualTo(("tgcd", "Mystery Disc (Japan).chd", "Mystery Disc (Japan).chd", IdentificationMethod.Manual)));
        Assert.That(File.Exists(Path.Combine(_factory.Root, "roms", "tgcd", "M", "Mystery Disc (Japan).chd")), Is.True);
        Assert.That(File.Exists(item.SourcePath), Is.False);
    }

    [Test]
    public async Task Chd_DatPick_IsStoredAsTheDatGameUnverified()
    {
        var item = await QueueChd("saturn", "quest.chd", 2403);

        var response = await _admin.ResolveAsync(item.Id, new { action = "Dat", datSystem = "saturn", datGameName = "Chd Quest (Japan)" });

        Assert.That(response.Code, Is.Null);
        var rom = (await ReadGame(response.GameId!.Value)).Roms.Single();
        Assert.That((rom.FileName, rom.Identification.Method, rom.Identification.DatGameName),
            Is.EqualTo(("Chd Quest (Japan) (Unverified).chd", IdentificationMethod.Manual, (string?)"Chd Quest (Japan)")));
        Assert.That(rom.Flags, Does.Contain("Unverified"));
        Assert.That(File.Exists(Path.Combine(_factory.Root, "roms", "saturn", "C", "Chd Quest (Japan) (Unverified).chd")), Is.True);
    }

    [Test]
    public async Task Chd_AttachedAsDisc2_ToABinCueGame_IsASecondRelease()
    {
        // Disc 1: an unmatched cue + bin set, resolved with Manual as one release.
        const string cue = "Game.cue", bin = "Game.bin";
        DropDisc("psx", bin, new SyntheticDiscBuilder { Seed = 2404 }.Build());
        var cueBytes = System.Text.Encoding.UTF8.GetBytes($"FILE \"{bin}\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
        DropDisc("psx", cue, cueBytes);
        await RunIngest();
        var cueItem = (await Reviews.GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(cueBytes)), default))!;
        var first = await _admin.ResolveAsync(cueItem.Id, new { action = "Manual", title = "Game", regions = new[] { "USA" }, languages = Array.Empty<string>(), flags = Array.Empty<string>(), disc = 1 });
        Assert.That(first.Code, Is.Null);
        var target = first.GameId!.Value;

        var item = await QueueChd("psx", "Game.chd", 2405);
        var response = await _admin.ResolveAsync(item.Id, new { action = "Attach", targetGameId = target, flag = "Disc", disc = 2 });

        Assert.That(response.Code, Is.Null);
        var game = await ReadGame(target);
        Assert.That(game.Roms, Has.Count.EqualTo(2));
        var rom = game.Roms.Single(r => r.Metadata.Sha1 == item.Sha1);
        Assert.That((rom.FileName, rom.ExternalFileName), Is.EqualTo(("Game (Disc 2).chd", "Game (Disc 2).chd")));
        Assert.That(game.Roms.Single(r => r.Metadata.Sha1 != item.Sha1).FileName, Is.EqualTo(bin), "disc 1 keeps its set name");
        Assert.That(rom.Flags, Is.EqualTo(new[] { "Disc 2" }));
        Assert.That(rom.Regions, Is.EqualTo(new[] { "USA" }));
    }

    [Test]
    public async Task Chd_CanBeDismissed()
    {
        var item = await QueueChd("segacd", "junk.chd", 2406);

        using var response = await _admin.PostAsync($"/api/review/{item.Id}/dismiss");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await Reviews.GetAsync(item.Id, default))!.Status, Is.EqualTo("Dismissed"));
    }

    [Test]
    public async Task Detail_OfASingleFile_IsDismissible_WithNoSet_B13()
    {
        var item = await Queue();

        using var detail = await _admin.GetAsync($"/api/review/{item.Id}");
        var json = await AuthClient.JsonAsync(detail);

        Assert.That((json.GetProperty("dismissible").GetBoolean(), json.GetProperty("fileSets").GetBoolean()), Is.EqualTo((true, false)));
        Assert.That(json.GetProperty("setMembers").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    private async Task WaitIdleAsync()
    {
        for (var i = 0; i < 600 && Service<JobRunner>().Current is not null; i++)
            await Task.Delay(50);
    }
}
