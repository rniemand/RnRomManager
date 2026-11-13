using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

[Category("Integration")]
public class IngestReviewTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] Unknown = new SyntheticRomBuilder { Seed = 929, Title = "TETRIS" }.Build();
    private static readonly byte[] Known = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private ApiFactory _factory = null!;
    private IIngestReviewRepository Reviews => Service<IIngestReviewRepository>();
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));

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
        LoadDats(Known);
    }

    private void LoadDats(byte[] bytes)
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
            [Gb] = [new FixtureGame("Tetris (World)", "Tetris (World).gb", bytes)],
            ["Nintendo - Game Boy Color"] = [],
        }, titleDb: IngestSeeder.EmptySwitch, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
    }

    private IngestFile Drop(string name = "mystery.gb", byte[]? bytes = null)
    {
        bytes ??= Unknown;
        var path = Path.Combine(_factory.Root, "ingest", "gb", name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return new IngestFile("gb", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
    }

    private Task<IngestItemResult?> Process(IngestFile file, Guid? job = null) =>
        Service<IngestPipeline>().ProcessAsync(file, job ?? Guid.NewGuid(), new CircuitBreaker(), CancellationToken.None);

    private async Task SetStatus(string status, string sha1)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        await db.ExecuteAsync("UPDATE ingest_review_items SET status = @status, game_id = @game, resolved_at = UTC_TIMESTAMP(), resolved_by = (SELECT MIN(id) FROM users) WHERE sha1 = @sha1",
            new { status, sha1, game = Guid.NewGuid() });
    }

    [Test]
    public async Task Unmatched_StoresHashesHeaderSuggestionsAndJob_LeavesSource()
    {
        var file = Drop();
        var job = Guid.NewGuid();
        var result = await Process(file, job);
        var row = (await Reviews.GetBySha1Async(Sha(Unknown), default))!;
        Assert.That((result!.Outcome, result.Reason), Is.EqualTo(("Review", "NoDatMatch")));
        Assert.That((row.SystemId, row.Status, row.SourcePath, row.OriginalFileName, row.LastSeenJobId),
            Is.EqualTo(("gb", "Pending", file.FullPath, file.Name, job)));
        Assert.That((row.SizeBytes, row.Crc32, row.Md5), Is.EqualTo(((long)Unknown.Length, Core.Storage.Crc32.ToHex(Unknown), Convert.ToHexStringLower(MD5.HashData(Unknown)))));
        Assert.That(JsonDocument.Parse(row.Header!).RootElement.GetProperty("title").GetString(), Is.EqualTo("TETRIS"));
        var suggestion = JsonDocument.Parse(row.Suggestions).RootElement[0];
        Assert.That(suggestion.GetProperty("romName").GetString(), Is.EqualTo("Tetris (World).gb"));
        Assert.That(suggestion.GetProperty("score").GetDouble(), Is.EqualTo(1.1));
        Assert.That(new object?[] { row.DatIdentification, row.CandidateGameIds, row.GameId, row.ResolvedAt, row.ResolvedBy }, Is.All.Null);
        Assert.That(File.Exists(file.FullPath), Is.True);
    }

    [TestCase("Pending")]
    [TestCase("Resolved")]
    [TestCase("Dismissed")]
    public async Task Reseen_UpsertsSameId_ResetsResolution_RefreshesSuggestions(string status)
    {
        var file = Drop();
        await Process(file);
        var before = (await Reviews.GetBySha1Async(Sha(Unknown), default))!;
        await SetStatus(status, before.Sha1);
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
            [Gb] = [new FixtureGame("Tetris Deluxe (World)", "Tetris Deluxe (World).gb", Known)],
            ["Nintendo - Game Boy Color"] = [],
        }, titleDb: IngestSeeder.EmptySwitch, layouts: IngestSeeder.RedumpLayouts);
        cache.Load();
        var job = Guid.NewGuid();
        await Process(file, job);
        var after = (await Reviews.GetBySha1Async(before.Sha1, default))!;
        Assert.That((after.Id, after.CreatedAt, after.LastSeenJobId, after.Status), Is.EqualTo((before.Id, before.CreatedAt, job, "Pending")));
        Assert.That(new object?[] { after.GameId, after.ResolvedAt, after.ResolvedBy }, Is.All.Null);
        Assert.That(after.Suggestions, Does.Contain("Tetris Deluxe"));
        Assert.That(await Reviews.ListPendingSourcesAsync(default), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task PendingFromAnotherSource_Duplicate_PreservesCanonicalReview()
    {
        var first = Drop();
        await Process(first);
        var copy = Drop("copy.gb");
        Assert.That((await Process(copy))!.Outcome, Is.EqualTo("Duplicate"));
        Assert.That(File.Exists(Path.Combine(_factory.Root, "ingest", "gb", "_duplicates", copy.Name)), Is.True);
        Assert.That((await Reviews.GetBySha1Async(Sha(Unknown), default))!.SourcePath, Is.EqualTo(first.FullPath));
    }

    [Test]
    public async Task DismissedLooseRedrop_UpdatesPaths_AndRequeues()
    {
        var old = Drop();
        await Process(old);
        await SetStatus("Dismissed", Sha(Unknown));
        File.Delete(old.FullPath);
        var next = Drop("new-location.gb");
        await Process(next);
        var row = (await Reviews.GetBySha1Async(Sha(Unknown), default))!;
        Assert.That((row.Status, row.SourcePath, row.ArchivePath, row.ArchiveEntry), Is.EqualTo(("Pending", next.FullPath, (string?)null, (string?)null)));
    }

    [TestCase("Pending")]
    [TestCase("Dismissed")]
    public async Task NowDatMatched_ResolvesWithGameId_AndNoResolver(string status)
    {
        var file = Drop();
        await Process(file);
        await SetStatus(status, Sha(Unknown));
        LoadDats(Unknown);
        var job = Guid.NewGuid();
        var result = await Process(file, job);
        var row = (await Reviews.GetBySha1Async(Sha(Unknown), default))!;
        Assert.That(result!.Outcome, Is.EqualTo("Ingested"));
        Assert.That((row.Status, row.GameId, row.ResolvedBy, row.LastSeenJobId), Is.EqualTo(("Resolved", result.GameId, (int?)null, job)));
        Assert.That(row.ResolvedAt, Is.Not.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingSourceAnotherOutcome_DeletesReview(bool invalid)
    {
        var bytes = invalid ? new SyntheticRomBuilder { Seed = 9, CorruptLogo = true, CorruptHeaderChecksum = true }.Build() : Known;
        var file = Drop(bytes: bytes);
        if (!invalid)
            await Process(Drop("library.gb", Known));
        await Reviews.UpsertAsync(new ReviewItem { SystemId = "gb", Reason = "NoDatMatch", SourcePath = file.FullPath,
            OriginalFileName = file.Name, Sha1 = Sha(bytes), Md5 = new string('0', 32), Crc32 = "00000000", SizeBytes = bytes.Length,
            CreatedAt = DateTime.UtcNow, LastSeenJobId = Guid.NewGuid() }, default);
        var result = await Process(file);
        Assert.That(result!.Outcome, Is.EqualTo(invalid ? "Failed" : "Duplicate"));
        Assert.That(await Reviews.GetBySha1Async(Sha(bytes), default), Is.Null);
    }

    [Test]
    public async Task ExtensionRename_ReviewPointsToRenamedSource_AndSurvivesPrepass()
    {
        var file = Drop(bytes: new SyntheticRomBuilder { Seed = 55, CgbFlag = 0xC0 }.Build());
        var result = await Process(file);
        var row = (await Reviews.GetBySha1Async(result!.Sha1!, default))!;
        Assert.That((row.SystemId, row.OriginalFileName), Is.EqualTo(("gbc", "mystery.gb")));
        Assert.That(row.SourcePath, Does.EndWith("mystery.gbc"));
        Assert.That(File.Exists(row.SourcePath), Is.True);
        await ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, Substitute.For<IJobItemRepository>())
            .RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);
        Assert.That((await Reviews.GetBySha1Async(result.Sha1!, default))!.Id, Is.EqualTo(row.Id));
    }

    [Test]
    public async Task AmbiguousGroup_StoresIdentityAndCandidates_NoSuggestionsOrZip()
    {
        var roms = Substitute.For<IRomRepository>();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        roms.ListGameIdsByBaseTitleAsync("gb", "Tetris", Arg.Any<CancellationToken>()).Returns(ids);
        var pipeline = ActivatorUtilities.CreateInstance<IngestPipeline>(_factory.Services, roms);
        var file = Drop(bytes: Known);
        var result = await pipeline.ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default);
        var row = (await Reviews.GetBySha1Async(Sha(Known), default))!;
        Assert.That((result!.Outcome, result.Reason), Is.EqualTo(("Review", "AmbiguousGroup")));
        Assert.That(JsonSerializer.Deserialize<Guid[]>(row.CandidateGameIds!), Is.EqualTo(ids));
        Assert.That(row.Suggestions, Is.EqualTo("[]"));
        var identity = JsonDocument.Parse(row.DatIdentification!).RootElement;
        Assert.That(identity.GetProperty("datGameName").GetString(), Is.EqualTo("Tetris (World)"));
        Assert.That(identity.GetProperty("datName").GetString(), Is.EqualTo(Gb));
        Assert.That(identity.GetProperty("datVersion").GetString(), Is.EqualTo(FixtureDatWriter.Version));
        Assert.That(identity.GetProperty("romName").GetString(), Is.EqualTo("Tetris (World).gb"));
        Assert.That(File.Exists(file.FullPath), Is.True);
        Assert.That(Directory.GetFiles(Path.Combine(_factory.Root, "roms"), "*.zip", SearchOption.AllDirectories), Is.Empty);
    }

    private string Archive(byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", "gb", "pack.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("games/mystery.gb").Open();
            stream.Write(bytes);
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }

    [Test]
    public async Task AllNullHeaders_UseIngestFoldersHandler_AndRemainPlausible()
    {
        var registry = new SystemRegistry([new Headerless("gb"), new Headerless("gbc")]);
        var pipeline = ActivatorUtilities.CreateInstance<IngestPipeline>(_factory.Services, registry);
        var file = Drop("headerless.gb", new byte[0x150]);
        var result = await pipeline.ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default);
        var row = (await Reviews.GetBySha1Async(result!.Sha1!, default))!;
        Assert.That((result.Outcome, result.SystemId, row.Header), Is.EqualTo(("Review", "gb", (string?)null)));
        Assert.That(File.Exists(file.FullPath), Is.True);
    }

    private sealed class Headerless(string id) : ISystemHandler
    {
        public string Id => id;
        public string DisplayName => id;
        public string Family => "gameboy";
        public IReadOnlyList<string> RomExtensions => ["." + id];
        public long MinRomSizeBytes => 0x150;
        public long MaxRomSizeBytes => 16 * 1024 * 1024;
        public string DatName => id == "gb" ? Gb : "Nintendo - Game Boy Color";
        public bool EmbedRomsInZip => true;
        public ReadOnlyMemory<byte> DefaultBoxArtPng => ReadOnlyMemory<byte>.Empty;
        public RomHeader? ReadHeader(ReadOnlySpan<byte> rom) => null;
    }

    [Test]
    public async Task PendingSourceError_DeletesOnlyItsReview_LeavesSourceForRetry()
    {
        var file = Drop();
        await Process(file);
        var other = Drop("other.gb", new SyntheticRomBuilder { Seed = 100 }.Build());
        var otherResult = await Process(other);
        LoadDats(Unknown);
        var roms = Substitute.For<IRomRepository>();
        var gameId = Guid.NewGuid();
        roms.ListGameIdsByBaseTitleAsync("gb", "Tetris", Arg.Any<CancellationToken>()).Returns(new[] { gameId });
        var pipeline = ActivatorUtilities.CreateInstance<IngestPipeline>(_factory.Services, roms);
        var result = await pipeline.ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default);
        Assert.That((result!.Outcome, result.Reason), Is.EqualTo(("Error", "TargetZipUnreadable")));
        Assert.That(await Reviews.GetBySha1Async(Sha(Unknown), default), Is.Null);
        Assert.That(await Reviews.GetBySha1Async(otherResult!.Sha1!, default), Is.Not.Null);
        Assert.That(File.Exists(file.FullPath), Is.True);
    }

    [Test]
    public async Task ArchiveRerun_UpsertsSameRowAndUpdatesJob_WithoutExtractedPaths()
    {
        var path = Archive(Unknown);
        var job = ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, Substitute.For<IJobItemRepository>());
        await job.RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);
        var before = (await Reviews.GetBySha1Async(Sha(Unknown), default))!;
        var jobId = Guid.NewGuid();
        await job.RunAsync(new JobContext(jobId, new JobProgress()), default);
        var after = (await Reviews.GetBySha1Async(Sha(Unknown), default))!;
        Assert.That((after.Id, after.LastSeenJobId, after.SourcePath), Is.EqualTo((before.Id, jobId, path)));
        Assert.That(File.Exists(path), Is.True);
        Assert.That(Directory.GetDirectories(Path.Combine(_factory.Root, "cache", "work")), Is.Empty);
    }

    [TestCase("Dismissed")]
    [TestCase("Resolved")]
    public async Task ArchiveReview_RerunAppliesDismissedOrResolvedFate(string status)
    {
        var path = Archive(Unknown);
        var items = new List<JobItem>();
        var repository = Substitute.For<IJobItemRepository>();
        repository.InsertAsync(Arg.Do<JobItem>(items.Add), Arg.Any<CancellationToken>()).Returns(1L);
        var job = ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, repository);
        await job.RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);
        var row = (await Reviews.GetBySha1Async(Sha(Unknown), default))!;
        Assert.That((row.SourcePath, row.ArchivePath, row.ArchiveEntry), Is.EqualTo((path, path, "games/mystery.gb")));
        Assert.That(items.Single().Outcome, Is.EqualTo("Review"));
        Assert.That(File.Exists(path), Is.True);
        if (status == "Resolved")
        {
            await SetStatus(status, row.Sha1);
            LoadDats(Unknown);
            await Process(Drop("resolved.gb"));
        }
        await SetStatus(status, row.Sha1);
        items.Clear();
        await job.RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);
        Assert.That(File.Exists(path), Is.False);
        Assert.That(items.Single().Outcome, Is.EqualTo(status == "Dismissed" ? "Failed" : "Duplicate"));
        Assert.That(File.Exists(Path.Combine(_factory.Root, "ingest", "gb", "_duplicates", "pack.zip")), Is.False);
        Assert.That(File.Exists(Path.Combine(_factory.Root, "ingest", "gb", "_failed", "pack.zip")), Is.EqualTo(status == "Dismissed"));
        Assert.That((await Reviews.GetBySha1Async(row.Sha1, default))!.Status, Is.EqualTo(status));
    }
}
