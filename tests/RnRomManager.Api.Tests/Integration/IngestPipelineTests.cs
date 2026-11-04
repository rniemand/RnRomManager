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

    [SetUp]
    public async Task SetUp()
    {
        _mirrorOverride = null;
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
        _factory.Services.GetRequiredService<IngestPipeline>().ProcessAsync(file, new CircuitBreaker(), CancellationToken.None);

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
}
