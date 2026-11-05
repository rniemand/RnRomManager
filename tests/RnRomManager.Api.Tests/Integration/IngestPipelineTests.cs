using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §7.2 loose-file happy path (S26): real DI wiring, MariaDB, temp library, fixture DATs, no network.</summary>
[Category("Integration")]
public class IngestPipelineTests
{
    private const string Gb = "Nintendo - Game Boy";
    private const string Gbc = "Nintendo - Game Boy Color";

    private static readonly byte[] TetrisJ = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] TetrisW = new SyntheticRomBuilder { Seed = 2, Title = "TETRIS" }.Build();
    private static readonly byte[] SeaQuest = new SyntheticRomBuilder { Seed = 3, Title = "SEAQUEST" }.Build();
    private static readonly byte[] Gold = new SyntheticRomBuilder { Seed = 4, Title = "POKEMON_GLD", CgbFlag = 0xC0 }.Build();

    private ApiFactory _factory = null!;
    private ILibraryMirror? _mirrorOverride;
    private Action<IServiceCollection>? _extraServices;

    [SetUp]
    public async Task SetUp()
    {
        _mirrorOverride = null;
        _extraServices = null;
        _factory = CreateFactory(await MariaDbServer.CreateDatabaseAsync());
        _ = _factory.Services;
        LoadDats();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private ApiFactory CreateFactory(string connectionString)
    {
        var factory = new ApiFactory(connectionString)
        {
            TestServices = services =>
            {
                if (_mirrorOverride is not null)
                    services.AddSingleton(_mirrorOverride);
                _extraServices?.Invoke(services);
            },
        };
        factory.Settings["Sources:ThumbnailBaseUrl"] = ""; // default art, no network
        return factory;
    }

    private void LoadDats()
    {
        var cache = _factory.Services.GetRequiredService<DatCache>();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.GbaDat] = [],
            [Gb] =
            [
                new FixtureGame("Tetris (Japan)", "Tetris (Japan).gb", TetrisJ) { Publishers = ["Nintendo"] },
                new FixtureGame("Tetris (World) (Rev 1)", "Tetris (World) (Rev 1).gb", TetrisW)
                {
                    ReleaseYear = 1989, Developers = ["Nintendo R&D1"], Publishers = ["Someone Else"], Genres = ["Puzzle"],
                },
                new FixtureGame("seaQuest DSV (USA, Europe)", "seaQuest DSV (USA, Europe).gb", SeaQuest),
            ],
            [Gbc] = [new FixtureGame("Pokemon - Gold Version (USA, Europe)", "Pokemon - Gold Version (USA, Europe).gbc", Gold) { ReleaseYear = 2000 }],
        });
        cache.Load();
    }

    private string Roms => Path.Combine(_factory.Root, "roms");

    private IngestFile Drop(string system, string name, byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", system, name);
        File.WriteAllBytes(path, bytes);
        return new IngestFile(system, path, name, bytes.Length, DateTime.UtcNow.AddMinutes(-1));
    }

    private Task<IngestItemResult?> IngestAsync(IngestFile file) =>
        _factory.Services.GetRequiredService<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), CancellationToken.None);

    private InfoJsonGame ReadZip(string relative)
    {
        using var zip = LibraryZip.Open(Path.Combine(Roms, relative), _factory.Services.GetRequiredService<InfoJsonReader>());
        return zip.InfoJson!.Game!;
    }

    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [Test]
    public async Task DatMatchedGbRom_BecomesANewGameZip_WithInfoJsonAndDbRows()
    {
        var file = Drop("gb", "tetris-jp.gb", TetrisJ);

        var result = await IngestAsync(file);

        Assert.That(result!.Outcome, Is.EqualTo(IngestOutcome.Ingested));
        Assert.That(result.SystemId, Is.EqualTo("gb"));
        Assert.That(result.Sha1, Is.EqualTo(Convert.ToHexStringLower(SHA1.HashData(TetrisJ))));
        Assert.That(File.Exists(file.FullPath), Is.False, "source deleted");

        var game = ReadZip("gb/T/Tetris.zip");
        Assert.That(game.GameId, Is.EqualTo(result.GameId));
        Assert.That((game.System, game.Title, game.DisplayTitle), Is.EqualTo(("gb", "Tetris", "Tetris")));
        Assert.That(game.Publishers, Is.EqualTo(new[] { "Nintendo" }));
        var rom = game.Roms.Single();
        Assert.That(rom.FileName, Is.EqualTo("Tetris (Japan).gb"));
        Assert.That(rom.OriginalFileName, Is.EqualTo("tetris-jp.gb"));
        Assert.That((rom.BaseTitle, rom.IsPrimary), Is.EqualTo(("Tetris", true)));
        Assert.That(rom.Regions, Is.EqualTo(new[] { "Japan" }));
        Assert.That(rom.Identification, Is.EqualTo(new InfoJsonIdentification(IdentificationMethod.Dat, Gb, FixtureDatWriter.Version, "Tetris (Japan)")));
        Assert.That(rom.Header!["title"]!.GetValue<string>(), Is.EqualTo("TETRIS"));
        Assert.That(rom.Metadata.SourcePath, Is.EqualTo(file.FullPath));
        Assert.That(game.Images.Single().IsDefault, Is.True);

        var row = await Service<IGameRepository>().GetAsync(game.GameId, CancellationToken.None);
        Assert.That((row!.ZipPath, row.Bucket, row.HasDefaultBoxArt), Is.EqualTo(("gb/T/Tetris.zip", "T", true)));
        Assert.That(row.ZipSizeBytes, Is.EqualTo(new FileInfo(Path.Combine(Roms, "gb", "T", "Tetris.zip")).Length));
        Assert.That(await Service<IRomRepository>().GetBySha1Async(result.Sha1!, CancellationToken.None), Is.Not.Null);
    }

    [Test]
    public async Task SecondRegion_JoinsTheZip_PrimaryRecomputed_MetadataOnlyFillsEmptyFields()
    {
        var first = await IngestAsync(Drop("gb", "a.gb", TetrisJ));
        var second = await IngestAsync(Drop("gb", "b.gb", TetrisW));

        Assert.That(second!.GameId, Is.EqualTo(first!.GameId));
        var game = ReadZip("gb/T/Tetris.zip");
        Assert.That(game.Roms.Select(r => (r.FileName, r.IsPrimary)), Is.EqualTo(new[]
        {
            ("Tetris (Japan).gb", false),
            ("Tetris (World) (Rev 1).gb", true), // World > Japan
        }));
        Assert.That(game.Publishers, Is.EqualTo(new[] { "Nintendo" }), "kept, not overwritten");
        Assert.That(game.Developers, Is.EqualTo(new[] { "Nintendo R&D1" }), "was empty, filled");
        Assert.That(game.ReleaseYear, Is.EqualTo(1989));
        Assert.That(game.Genres, Is.EqualTo(new[] { "Puzzle" }));
        Assert.That(game.Metadata.UpdatedAt, Is.GreaterThanOrEqualTo(game.Metadata.CreatedAt));
        Assert.That(await Service<IRomRepository>().ListByGameAsync(game.GameId, CancellationToken.None), Has.Count.EqualTo(2));
        Assert.That(Directory.GetFiles(Path.Combine(Roms, "gb", "T")), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task GbcRomDroppedInGbFolder_LandsUnderGbc()
    {
        var result = await IngestAsync(Drop("gb", "gold.gbc", Gold));

        Assert.That(result!.SystemId, Is.EqualTo("gbc"));
        var game = ReadZip("gbc/P/Pokemon - Gold Version.zip");
        Assert.That(game.System, Is.EqualTo("gbc"));
        Assert.That(game.ReleaseYear, Is.EqualTo(2000));
    }

    [Test]
    public async Task LowercaseFirstLetter_GoesToTheUppercaseBucket()
    {
        await IngestAsync(Drop("gb", "sq.gb", SeaQuest));

        Assert.That(File.Exists(Path.Combine(Roms, "gb", "S", "seaQuest DSV.zip")), Is.True);
    }

    [Test]
    public async Task ZipNameClash_AnyCase_GetsASuffix()
    {
        Directory.CreateDirectory(Path.Combine(Roms, "gb", "T"));
        File.WriteAllBytes(Path.Combine(Roms, "gb", "T", "TETRIS.zip"), [0]);

        await IngestAsync(Drop("gb", "a.gb", TetrisJ));

        Assert.That(ReadZip("gb/T/Tetris (2).zip").Title, Is.EqualTo("Tetris"));
    }

    [Test]
    public async Task TargetZipUnreadable_IsError_SourceUntouched()
    {
        await IngestAsync(Drop("gb", "a.gb", TetrisJ));
        File.WriteAllBytes(Path.Combine(Roms, "gb", "T", "Tetris.zip"), [1, 2, 3]);
        var file = Drop("gb", "b.gb", TetrisW);

        var result = await IngestAsync(file);

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Error, IngestErrors.TargetZipUnreadable)));
        Assert.That(File.Exists(file.FullPath), Is.True);
    }

    [Test]
    public async Task TargetZipNewerSchema_IsError_SourceUntouched()
    {
        await IngestAsync(Drop("gb", "a.gb", TetrisJ));
        var path = Path.Combine(Roms, "gb", "T", "Tetris.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("info.json")!;
            JsonNode json;
            using (var s = entry.Open())
                json = JsonNode.Parse(s)!;
            json["schemaVersion"] = 99;
            entry.Delete();
            using var w = zip.CreateEntry("info.json").Open();
            w.Write(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        }
        var file = Drop("gb", "b.gb", TetrisW);

        var result = await IngestAsync(file);

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Error, IngestErrors.InfoJsonNewerSchema)));
        Assert.That(File.Exists(file.FullPath), Is.True);
    }

    [Test]
    public async Task DbWriteFailed_StopsTheJob_ZipKept_SourceKept_LaterFilesUntouched()
    {
        var connectionString = _factory.Settings["ConnectionStrings:Default"]!;
        _factory.Dispose();
        _mirrorOverride = Substitute.For<ILibraryMirror>();
        _mirrorOverride.ApplyAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new TimeoutException("DB down"));
        _mirrorOverride.ReplaceAsync(default!, default).ThrowsAsyncForAnyArgs(new TimeoutException("DB down")); // a substitute intercepts the default method too
        _factory = CreateFactory(connectionString);
        _ = _factory.Services;
        LoadDats();
        var first = Drop("gb", "a.gb", TetrisJ);
        File.SetLastWriteTimeUtc(first.FullPath, DateTime.UtcNow.AddMinutes(-1));
        var later = Drop("gb", "z.gb", SeaQuest);
        File.SetLastWriteTimeUtc(later.FullPath, DateTime.UtcNow.AddMinutes(-1));
        var items = Substitute.For<IJobItemRepository>();
        var job = ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, items);

        var ex = await Assert.ThrowsAsync<IngestStoppedException>(() =>
            job.RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("DB write failed"));
        await items.Received(1).InsertAsync(Arg.Is<JobItem>(i => i.Outcome == IngestOutcome.Error && i.Reason == IngestErrors.DbWriteFailed),
            Arg.Any<CancellationToken>());
        await items.Received(1).InsertAsync(Arg.Any<JobItem>(), Arg.Any<CancellationToken>());
        Assert.That(File.Exists(first.FullPath) && File.Exists(later.FullPath), Is.True);
        Assert.That(File.Exists(Path.Combine(Roms, "gb", "T", "Tetris.zip")), Is.True, "the ZIP stays; the next pre-pass repairs the DB");
    }

    // --- S27: failures, duplicates, self-repair ---

    private string Ingest(string system) => Path.Combine(_factory.Root, "ingest", system);

    [Test]
    public async Task UnsupportedType_MovesToFailed_WithErrorTxt()
    {
        var jobId = Guid.NewGuid();
        var file = Drop("gb", "readme.txt", [1, 2, 3]);

        var result = await _factory.Services.GetRequiredService<IngestPipeline>().ProcessAsync(file, jobId, new CircuitBreaker(), CancellationToken.None);

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
        Assert.That(File.Exists(file.FullPath), Is.False);
        var moved = Path.Combine(Ingest("gb"), "_failed", "readme.txt");
        Assert.That(File.Exists(moved), Is.True);
        var errorTxt = File.ReadAllText(moved + ".error.txt");
        Assert.That(errorTxt, Does.Contain("UnsupportedType").And.Contain(jobId.ToString()).And.Contain("time: "));
    }

    [Test]
    public async Task GbaFile_InTheGbFolder_IsUnsupportedType()
    {
        var file = Drop("gb", "courier.gba", new SyntheticGbaRomBuilder { Title = "STARCOURIER" }.Build());

        var result = await IngestAsync(file);

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
        Assert.That(File.ReadAllText(Path.Combine(Ingest("gb"), "_failed", "courier.gba.error.txt")), Does.Contain("gameboy"));
    }

    [Test]
    public async Task FailedNameClash_GetsASuffix()
    {
        Directory.CreateDirectory(Path.Combine(Ingest("gb"), "_failed"));
        File.WriteAllBytes(Path.Combine(Ingest("gb"), "_failed", "README.TXT"), [0]);

        await IngestAsync(Drop("gb", "readme.txt", [1]));

        Assert.That(File.Exists(Path.Combine(Ingest("gb"), "_failed", "readme (2).txt")), Is.True);
        Assert.That(File.Exists(Path.Combine(Ingest("gb"), "_failed", "readme (2).txt.error.txt")), Is.True);
    }

    [Test]
    public async Task TooSmall_And_TooLarge_Fail()
    {
        var small = await IngestAsync(Drop("gb", "tiny.gb", new byte[0x14F]));
        var big = Drop("gb", "huge.gb", [1]) with { SizeBytes = 16L * 1024 * 1024 + 1 };
        var large = await IngestAsync(big);

        Assert.That(small!.Reason, Is.EqualTo(IngestFailures.TooSmall));
        Assert.That(large!.Reason, Is.EqualTo(IngestFailures.TooLarge));
        Assert.That(Directory.GetFiles(Path.Combine(Ingest("gb"), "_failed"), "*.gb"), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task UnmatchedWithInvalidLogoAndChecksum_IsInvalidHeader()
    {
        var garbage = new SyntheticRomBuilder { Seed = 50, CorruptLogo = true, CorruptHeaderChecksum = true }.Build();

        var result = await IngestAsync(Drop("gb", "garbage.gb", garbage));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.InvalidHeader)));
        Assert.That(result.Sha1, Is.EqualTo(Convert.ToHexStringLower(SHA1.HashData(garbage))));
        Assert.That(File.Exists(Path.Combine(Ingest("gb"), "_failed", "garbage.gb")), Is.True);
    }

    [Test]
    public async Task UnmatchedPlausible_WrongExtension_IsRenamedAndWaits()
    {
        var gbcOnly = new SyntheticRomBuilder { Seed = 51, CgbFlag = 0xC0 }.Build();
        var file = Drop("gb", "mystery.gb", gbcOnly);

        var result = await IngestAsync(file);

        Assert.That((result!.Outcome, result.Reason, result.SystemId), Is.EqualTo((IngestOutcome.Review, "NoDatMatch", "gbc")));
        Assert.That(File.Exists(file.FullPath), Is.False);
        Assert.That(File.Exists(Path.Combine(Ingest("gb"), "mystery.gbc")), Is.True);
    }

    [Test]
    public async Task SameSha1AgainIsADuplicate_MovedToDuplicates()
    {
        var first = await IngestAsync(Drop("gb", "a.gb", TetrisJ));
        var copy = Drop("gb", "copy.gb", TetrisJ);

        var result = await IngestAsync(copy);

        Assert.That(result!.Outcome, Is.EqualTo(IngestOutcome.Duplicate));
        Assert.That(result.GameId, Is.EqualTo(first!.GameId));
        Assert.That(result.Details, Does.Contain("gb/T/Tetris.zip"));
        Assert.That(File.Exists(Path.Combine(Ingest("gb"), "_duplicates", "copy.gb")), Is.True);
        Assert.That(File.Exists(copy.FullPath), Is.False);
    }

    [Test]
    public async Task OpenHashMismatch_SelfRepairsTheEntry_AndResolvesTheIssue()
    {
        await IngestAsync(Drop("gb", "a.gb", TetrisJ));
        var zipPath = Path.Combine(Roms, "gb", "T", "Tetris.zip");
        CorruptEntry(zipPath, "roms/Tetris (Japan).gb", TetrisJ.Length);
        var issueId = await InsertIssueAsync("HashMismatch", "gb/T/Tetris.zip", "roms/Tetris (Japan).gb");
        var copy = Drop("gb", "good-copy.gb", TetrisJ);

        var result = await IngestAsync(copy);

        Assert.That(result!.Outcome, Is.EqualTo(IngestOutcome.Repaired), result.Details);
        Assert.That(File.Exists(copy.FullPath), Is.False);
        using (var zip = LibraryZip.Open(zipPath, Service<InfoJsonReader>()))
        using (var entry = zip.OpenEntry("roms/Tetris (Japan).gb", TetrisJ.Length))
        using (var buffer = new MemoryStream())
        {
            entry.CopyTo(buffer);
            Assert.That(buffer.ToArray(), Is.EqualTo(TetrisJ));
        }
        Assert.That(await IssueStatusAsync(issueId), Is.EqualTo("Resolved"));
    }

    [Test]
    public async Task ReadFailure_IsErrorIoError()
    {
        var gone = new IngestFile("gb", Path.Combine(Ingest("gb"), "vanished.gb"), "vanished.gb", TetrisJ.Length, DateTime.UtcNow.AddMinutes(-1));

        var result = await IngestAsync(gone);

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Error, IngestErrors.IoError)));
    }

    [Test]
    [Platform("Win")] // only Windows refuses to delete a file another handle has open without FileShare.Delete
    public async Task DeleteFailure_IsAWarning_ItemStillIngested()
    {
        var file = Drop("gb", "a.gb", TetrisJ);
        using var held = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await IngestAsync(file);

        Assert.That(result!.Outcome, Is.EqualTo(IngestOutcome.Ingested));
        Assert.That(File.Exists(file.FullPath), Is.True);
    }

    [Test]
    public async Task UnexpectedException_IsErrorUnexpected_AndTheJobContinues()
    {
        var connectionString = _factory.Settings["ConnectionStrings:Default"]!;
        _factory.Dispose();
        var roms = Substitute.For<IRomRepository>();
        roms.ListSha1sAsync(default).ReturnsForAnyArgs([]);
        roms.GetBySha1Async(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("boom"));
        _extraServices = services => services.AddSingleton(roms);
        _factory = CreateFactory(connectionString);
        _ = _factory.Services;
        LoadDats();
        foreach (var name in new[] { "a.gb", "b.gb" })
            File.SetLastWriteTimeUtc(Drop("gb", name, TetrisJ).FullPath, DateTime.UtcNow.AddMinutes(-1));
        var items = Substitute.For<IJobItemRepository>();
        var job = ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, items);

        var summary = (Dictionary<string, int>)(await job.RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None))!;

        Assert.That(summary[IngestOutcome.Error], Is.EqualTo(2));
        await items.Received(2).InsertAsync(Arg.Is<JobItem>(i => i.Reason == IngestErrors.Unexpected && i.Details == "boom"), Arg.Any<CancellationToken>());
    }

    private static void CorruptEntry(string zipPath, string entryName, int length)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        zip.GetEntry(entryName)!.Delete();
        using var w = zip.CreateEntry(entryName).Open();
        w.Write(new byte[length]); // same size, wrong bytes
    }

    private async Task<long> InsertIssueAsync(string type, string zipPath, string entryName)
    {
        await using var connection = new MySqlConnector.MySqlConnection(_factory.Settings["ConnectionStrings:Default"]);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO integrity_issues (type, severity, system_id, zip_path, entry_name, status, first_seen_job_id, last_seen_job_id, detected_at) "
            + "VALUES (@type, 'Error', 'gb', @zip, @entry, 'Open', @job, @job, UTC_TIMESTAMP(6)); SELECT LAST_INSERT_ID();";
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@zip", zipPath);
        command.Parameters.AddWithValue("@entry", entryName);
        command.Parameters.AddWithValue("@job", Guid.NewGuid().ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string?> IssueStatusAsync(long id)
    {
        await using var connection = new MySqlConnector.MySqlConnection(_factory.Settings["ConnectionStrings:Default"]);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM integrity_issues WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return (string?)await command.ExecuteScalarAsync();
    }
}
