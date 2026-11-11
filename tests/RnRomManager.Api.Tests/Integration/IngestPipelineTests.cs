using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Users;
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
    private static readonly SyntheticN64RomBuilder Kart = new() { Seed = 64, SizeBytes = 8192, Title = "KART", GameCode = "NKTE" };
    private static readonly byte[] Gold = new SyntheticRomBuilder { Seed = 4, Title = "POKEMON_GLD", CgbFlag = 0xC0 }.Build();
    private static readonly byte[] Pioneer = new SyntheticNesRomBuilder { Seed = 70, Mapper = 1 }.Build();
    /// <summary>The same dump without its 16-byte header: what the DAT's <c>.unh</c> entry hashes.</summary>
    private static readonly byte[] PioneerHeaderless = Pioneer[16..];
    private static readonly SyntheticSnesRomBuilder Rally = new() { Seed = 80, SizeKiB = 64, Title = "MODE SEVEN RALLY" };

    private static readonly byte[] Lanes = Atari(4096, 120);
    private static readonly byte[] Pursuit = Coleco(8192, 130, 0xAA, 0x55);
    private static readonly byte[] Drift = GameGear(32768, 140, header: true);
    /// <summary>An SMS-mode Game Gear game: the DAT names it <c>.sms</c> (G4).</summary>
    private static readonly byte[] Tide = GameGear(32768, 141, header: false);
    private static readonly byte[] Harbor = GameGear(32768, 150, header: true);
    private static readonly byte[] Comet = new SyntheticMegaDriveRomBuilder { Seed = 160, Size = 65536 }.Build();
    private static readonly byte[] Relay = new SyntheticMegaDriveRomBuilder { Seed = 170, Size = 65536, SystemType = "SEGA 32X", OverseasTitle = "STAR RELAY" }.Build();
    private static readonly byte[] Circuit = Atari(262144, 180);
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
            [IngestSeeder.N64Dat] = [new FixtureGame("Kart 64 (USA)", "Kart 64 (USA).z64", Kart.Build())],
            // N3: the .unh entry is left out of the index, so its bytes never match.
            [IngestSeeder.NesDat] =
            [
                new FixtureGame("Pioneer (USA)", "Pioneer (USA).nes", Pioneer),
                new FixtureGame("Pioneer (USA)", "Pioneer (USA).unh", PioneerHeaderless),
            ],
            [IngestSeeder.SnesDat] = [new FixtureGame("Mode Seven Rally (USA)", "Mode Seven Rally (USA).sfc", Rally.Build())],
            [IngestSeeder.Atari2600Dat] = [new FixtureGame("Laser Lanes (USA)", "Laser Lanes (USA).a26", Lanes)],
            [IngestSeeder.ColecoVisionDat] = [new FixtureGame("Pixel Pursuit (USA)", "Pixel Pursuit (USA).col", Pursuit)],
            [IngestSeeder.GameGearDat] =
            [
                new FixtureGame("Pocket Drift (World)", "Pocket Drift (World).gg", Drift),
                new FixtureGame("Tide Runner (World)", "Tide Runner (World).sms", Tide),
            ],
            [IngestSeeder.MasterSystemDat] = [new FixtureGame("Harbor Patrol (World)", "Harbor Patrol (World).sms", Harbor)],
            [IngestSeeder.MegaDriveDat] = [new FixtureGame("Turbo Comet (USA, Europe)", "Turbo Comet (USA, Europe).md", Comet)],
            [IngestSeeder.Sega32xDat] = [new FixtureGame("Star Relay (USA)", "Star Relay (USA).32x", Relay)],
            [IngestSeeder.TurboGrafx16Dat] = [new FixtureGame("Neon Circuit (USA)", "Neon Circuit (USA).pce", Circuit)],
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
        }, titleDb: IngestSeeder.EmptySwitch);
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

    private sealed record AuditRow(string Action, string? SystemId, Guid? GameId, int? UserId, string? Username, Guid? JobId, string? Path,
        string? TargetPath, string? Details);

    /// <summary>AU4 rows written so far, oldest first (after the writer drained the queue).</summary>
    private async Task<List<AuditRow>> AuditRowsAsync()
    {
        await Service<RnRomManager.Api.AuditWriter>().FlushAsync();
        await using var db = Service<IDbConnectionFactory>().Create();
        return (await db.QueryAsync<AuditRow>("""
            SELECT action AS Action, system_id AS SystemId, game_id AS GameId, user_id AS UserId, username AS Username, job_id AS JobId,
                   path AS Path, target_path AS TargetPath, details AS Details
            FROM audit_events ORDER BY id
            """)).AsList();
    }

    private InfoJsonGame ReadZip(string relative)
    {
        using var zip = LibraryZip.Open(Path.Combine(Roms, relative), _factory.Services.GetRequiredService<InfoJsonReader>());
        return zip.InfoJson!.Game!;
    }

    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    private static byte[] Atari(int size, int seed)
    {
        var rom = new byte[size];
        new Random(seed).NextBytes(rom);
        return rom;
    }

    private static byte[] GameGear(int size, int seed, bool header)
    {
        var rom = Atari(size, seed);
        if (header)
            "TMR SEGA"u8.CopyTo(rom.AsSpan(0x7FF0));
        else if (size >= 0x7FF8)
            rom.AsSpan(0x7FF0, 8).Clear(); // no "TMR SEGA" by chance
        return rom;
    }

    private async Task<ReviewItem> GameGearReviewAsync(string name, int seed)
    {
        var bytes = GameGear(32768, seed, header: false);
        var result = await IngestAsync(Drop("gg", name, bytes));
        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Review, "NoDatMatch")));
        return (await Service<IIngestReviewRepository>().GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!;
    }

    private async Task<InfoJsonRom> ResolveGameGearAsync(ReviewItem item, ReviewResolutionRequest request)
    {
        var user = await TestUsers.CreateAsync(_factory, $"gg-{Guid.NewGuid():N}"[..20], TestUsers.AllRoles);
        var resolved = await Service<ReviewResolver>().ResolveAsync(item.Id, request, new AuditActor(user.Id, user.Username, null), default);
        Assert.That(resolved.Code, Is.Null, string.Join("; ", resolved.Errors?.SelectMany(e => e.Value) ?? []));
        var row = (await Service<IGameRepository>().GetAsync(resolved.GameId!.Value, default))!;
        return ReadZip(row.ZipPath).Roms.Single(r => r.Metadata.Sha1 == item.Sha1);
    }

    private static ReviewResolutionRequest GameGearManual(string title) =>
        new() { Action = "Manual", Title = title, Regions = ["World"], Languages = [], Flags = [] };

    [TestCase("nes", "drift.gg")]
    [TestCase("gg", "pioneer.nes")]
    public async Task GameGear_FileInAnotherFamilysFolder_IsUnsupportedType(string folder, string name)
    {
        var result = await IngestAsync(Drop(folder, name, name.EndsWith(".nes") ? Pioneer : Drift));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
    }

    [TestCase(8191, "TooSmall")]
    [TestCase(1048577, "TooLarge")]
    public async Task GameGear_OutsideTheSizeLimits_Fails(int size, string reason)
    {
        var result = await IngestAsync(Drop("gg", "odd.gg", GameGear(size, 142, header: false)));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, reason)));
    }

    [Test]
    public async Task GameGear_UnmatchedWithoutHeader_GoesToReview_AndManualIsStoredAsGg()
    {
        var item = await GameGearReviewAsync("Pocket Drift Hack.gg", 143);
        Assert.That(item.Header, Is.Null);
        Assert.That(item.Suggestions, Does.Contain("Pocket Drift"));

        var rom = await ResolveGameGearAsync(item, GameGearManual("Pocket Drift Hack"));

        Assert.That((rom.FileName, rom.OriginalFileName), Is.EqualTo(("Pocket Drift Hack (World).gg", "Pocket Drift Hack.gg")));
    }

    [Test]
    public async Task GameGear_UnmatchedSms_ManualIsStoredAsGg()
    {
        var item = await GameGearReviewAsync("Harbour Hop.sms", 144);

        var rom = await ResolveGameGearAsync(item, GameGearManual("Harbour Hop"));

        Assert.That(rom.FileName, Is.EqualTo("Harbour Hop (World).gg"));
    }

    [Test]
    public async Task GameGear_DatAction_KeepsTheDatRomNamesSmsExtension()
    {
        var item = await GameGearReviewAsync("tide-runner-alt.gg", 145);

        var rom = await ResolveGameGearAsync(item,
            new ReviewResolutionRequest { Action = "Dat", DatSystem = "gg", DatGameName = "Tide Runner (World)" });

        Assert.That(rom.FileName, Is.EqualTo("Tide Runner (World) (Unverified).sms"));
    }

    [Test]
    public async Task GameGear_AttachAmbiguousGroup_KeepsTheDatRomNamesSmsExtension()
    {
        var target = await GameGearReviewAsync("Tide Runner.gg", 146);
        var targetRom = await ResolveGameGearAsync(target, GameGearManual("Tide Runner"));
        var targetId = (await Service<IIngestReviewRepository>().GetAsync(target.Id, default))!.GameId!.Value;
        var item = await GameGearReviewAsync("tide-runner-world.sms", 147);
        await Service<IIngestReviewRepository>().UpsertAsync(item with
        {
            Reason = "AmbiguousGroup", Suggestions = "[]", CandidateGameIds = JsonSerializer.Serialize(new[] { targetId, Guid.NewGuid() }),
            DatIdentification = JsonSerializer.Serialize(new
            {
                datName = IngestSeeder.GameGearDat, datVersion = FixtureDatWriter.Version, datGameName = "Tide Runner (World)",
                romName = "Tide Runner (World).sms",
            }),
        }, default);

        var rom = await ResolveGameGearAsync(item,
            new ReviewResolutionRequest { Action = "Attach", TargetGameId = targetId, Flag = "None" });

        Assert.That(targetRom.FileName, Is.EqualTo("Tide Runner (World).gg"));
        Assert.That(rom.FileName, Is.EqualTo("Tide Runner (World).sms"));
    }

    [Test]
    public async Task GameGear_ZipHoldingADatGame_IsIngested_WithItsHeader()
    {
        var path = Path.Combine(Ingest("gg"), "drift.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("Pocket Drift (World).gg").Open())
            entry.Write(Drift);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        await ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, Substitute.For<IJobItemRepository>())
            .RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);

        Assert.That(File.Exists(path), Is.False, "fully ingested archive is deleted");
        var game = ReadZip(Path.Combine("gg", "P", "Pocket Drift.zip"));
        Assert.That((game.System, game.Roms.Single().FileName), Is.EqualTo(("gg", "Pocket Drift (World).gg")));
        Assert.That(game.Roms.Single().Header!["offset"]!.GetValue<string>(), Is.EqualTo("7FF0"));
    }

    private async Task<ReviewItem> ReviewAsync(string system, string name, byte[] bytes)
    {
        var result = await IngestAsync(Drop(system, name, bytes));
        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Review, "NoDatMatch")));
        return (await Service<IIngestReviewRepository>().GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!;
    }

    private async Task RunIngestJobAsync() =>
        await ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, Substitute.For<IJobItemRepository>())
            .RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);

    private string Zip(string system, string name, params (string Entry, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(Ingest(system), name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (entry, bytes) in entries)
                using (var stream = zip.CreateEntry(entry).Open())
                    stream.Write(bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }

    [TestCase("nes", "harbor.sms")]
    [TestCase("sms", "comet.md")]
    [TestCase("md", "comet.smd")]
    [TestCase("md", "relay.32x")]
    [TestCase("s32x", "comet.md")]
    [TestCase("s32x", "comet.bin")]
    [TestCase("s32x", "comet.gen")]
    public async Task SegaFile_InTheWrongFolderOrUnsupported_IsUnsupportedType(string folder, string name)
    {
        var result = await IngestAsync(Drop(folder, name, name.EndsWith(".sms") ? Harbor : Comet));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
    }

    [Test]
    public async Task GameGear_SmsMatchingOnlyTheGgDat_IsStillGg()
    {
        var result = await IngestAsync(Drop("gg", "tide.sms", Tide));

        Assert.That(result!.Outcome, Is.EqualTo(IngestOutcome.Ingested));
        var game = ReadZip(Path.Combine("gg", "T", "Tide Runner.zip"));
        Assert.That((game.System, game.Roms.Single().FileName), Is.EqualTo(("gg", "Tide Runner (World).sms")));
    }

    [TestCase("sms", "odd.sms", 2047, "TooSmall")]
    [TestCase("sms", "odd.sms", 4194305, "TooLarge")]
    [TestCase("md", "odd.md", 8191, "TooSmall")]
    [TestCase("md", "odd.md", 16777217, "TooLarge")]
    [TestCase("s32x", "odd.32x", 16383, "TooSmall")]
    [TestCase("s32x", "odd.32x", 4194305, "TooLarge")]
    public async Task Sega_OutsideTheSizeLimits_Fails(string system, string name, int size, string reason)
    {
        var result = await IngestAsync(Drop(system, name, GameGear(size, 151, header: false)));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, reason)));
    }

    [Test]
    public async Task MasterSystem_DatMatch_IsIngestedWithItsHeader()
    {
        var result = await IngestAsync(Drop("sms", "harbor.sms", Harbor));

        Assert.That(result!.Outcome, Is.EqualTo(IngestOutcome.Ingested));
        var game = ReadZip(Path.Combine("sms", "H", "Harbor Patrol.zip"));
        Assert.That((game.System, game.Roms.Single().FileName), Is.EqualTo(("sms", "Harbor Patrol (World).sms")));
        Assert.That(game.Roms.Single().Header!["offset"]!.GetValue<string>(), Is.EqualTo("7FF0"));
    }

    [Test]
    public async Task MasterSystem_UnmatchedWithoutHeader_GoesToReview_AndManualIsStoredAsSms()
    {
        var item = await ReviewAsync("sms", "Harbor Patrol Hack.sms", GameGear(32768, 152, header: false));
        Assert.That(item.Header, Is.Null);

        var rom = await ResolveGameGearAsync(item, GameGearManual("Harbor Patrol Hack"));

        Assert.That((rom.FileName, rom.OriginalFileName), Is.EqualTo(("Harbor Patrol Hack (World).sms", "Harbor Patrol Hack.sms")));
    }

    [Test]
    public async Task MegaDrive_UnmatchedHeaderedBin_GoesToReview_WithTheHeaderTitle_AndManualIsStoredAsMd()
    {
        var item = await ReviewAsync("md", "mystery.bin", new SyntheticMegaDriveRomBuilder { Seed = 161, Size = 65536 }.Build());
        Assert.That(item.Suggestions, Does.Contain("Turbo Comet"));

        var rom = await ResolveGameGearAsync(item, GameGearManual("Turbo Comet Proto"));

        Assert.That((rom.FileName, rom.OriginalFileName), Is.EqualTo(("Turbo Comet Proto (World).md", "mystery.bin")));
    }

    [Test]
    public async Task Sega32x_UnmatchedHeadered_GoesToReview_WithTheHeaderTitle_AndManualIsStoredAs32x()
    {
        var item = await ReviewAsync("s32x", "mystery.32x",
            new SyntheticMegaDriveRomBuilder { Seed = 171, Size = 65536, SystemType = "SEGA 32X", OverseasTitle = "STAR RELAY" }.Build());
        Assert.That(item.Suggestions, Does.Contain("Star Relay"));

        var rom = await ResolveGameGearAsync(item, GameGearManual("Star Relay Proto"));

        Assert.That((rom.FileName, rom.OriginalFileName), Is.EqualTo(("Star Relay Proto (World).32x", "mystery.32x")));
    }

    [Test]
    public async Task Sega32x_DatMatch_IsStoredUnderS32x_WithItsHeader()
    {
        var result = await IngestAsync(Drop("s32x", "relay.32x", Relay));

        Assert.That(result!.Outcome, Is.EqualTo(IngestOutcome.Ingested));
        var game = ReadZip(Path.Combine("s32x", "S", "Star Relay.zip"));
        Assert.That((game.System, game.Roms.Single().FileName), Is.EqualTo(("s32x", "Star Relay (USA).32x")));
        Assert.That(game.Roms.Single().Header!["systemType"]!.GetValue<string>(), Is.EqualTo("SEGA 32X"));
    }

    [Test]
    public async Task MegaDrive_DatMatchedBin_IsStoredAsMd_WithItsHeader()
    {
        var result = await IngestAsync(Drop("md", "comet.bin", Comet));

        Assert.That(result!.Outcome, Is.EqualTo(IngestOutcome.Ingested));
        var game = ReadZip(Path.Combine("md", "T", "Turbo Comet.zip"));
        Assert.That((game.System, game.Roms.Single().FileName), Is.EqualTo(("md", "Turbo Comet (USA, Europe).md")));
        Assert.That(game.Roms.Single().Header!["serial"]!.GetValue<string>(), Is.EqualTo("GM 00001234-00"));
    }

    [Test]
    public async Task MegaDrive_ZipHoldingADatGame_IsIngested()
    {
        var path = Zip("md", "comet.zip", ("Turbo Comet (USA, Europe).md", Comet));

        await RunIngestJobAsync();

        Assert.That(File.Exists(path), Is.False, "fully ingested archive is deleted");
        Assert.That(ReadZip(Path.Combine("md", "T", "Turbo Comet.zip")).System, Is.EqualTo("md"));
    }

    [Test]
    public async Task MegaDrive_ZipWithAReadmeMd_IngestsTheGame_AndFailsTheReadmeAsTooSmall()
    {
        var path = Zip("md", "comet.zip", ("Turbo Comet (USA, Europe).md", Comet), ("README.md", new byte[100]));

        await RunIngestJobAsync();

        Assert.That(ReadZip(Path.Combine("md", "T", "Turbo Comet.zip")).System, Is.EqualTo("md"));
        var failed = Path.Combine(Ingest("md"), "_failed", "comet.zip");
        Assert.That((File.Exists(path), File.Exists(failed)), Is.EqualTo((false, true)));
        Assert.That(File.ReadAllText(failed + ".error.txt"),
            Does.Contain("Turbo Comet (USA, Europe).md: Ingested").And.Contain("README.md: Failed TooSmall"));
    }

    private static byte[] Coleco(int size, int seed, byte first, byte second)
    {
        var rom = Atari(size, seed);
        (rom[0], rom[1]) = (first, second);
        return rom;
    }

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
    public async Task Ingested_IsAudited_WithRelativePathsAndTheRelease()
    {
        var jobId = Guid.NewGuid();
        var result = await Service<IngestPipeline>().ProcessAsync(Drop("gb", "a.gb", TetrisJ), jobId, new CircuitBreaker(), CancellationToken.None);

        var row = (await AuditRowsAsync()).Single();
        Assert.That(row, Is.EqualTo(new AuditRow(AuditActions.Ingested, "gb", result!.GameId, null, null, jobId, "ingest/gb/a.gb",
            "roms/gb/T/Tetris.zip", "release Tetris (Japan).gb")));
    }

    [Test]
    public async Task AnAdminStartedRun_AuditsEveryFileAction_WithTheJobAndUsername()
    {
        var boss = await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        foreach (var (name, bytes) in new[] { ("a.gb", TetrisJ), ("b.gb", TetrisJ), ("readme.txt", new byte[] { 1, 2, 3 }) })
            File.SetLastWriteTimeUtc(Drop("gb", name, bytes).FullPath, DateTime.UtcNow.AddMinutes(-1));
        var pack = Path.Combine(Ingest("gb"), "pack.zip");
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        await using (var entry = zip.CreateEntry("nested/sea.gb").Open())
            await entry.WriteAsync(SeaQuest);
        File.SetLastWriteTimeUtc(pack, DateTime.UtcNow.AddMinutes(-1));

        var started = await Service<JobRunner>().StartAsync(ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services), JobTrigger.Manual,
            boss.Id, CancellationToken.None);
        await started!.Completion;

        var rows = await AuditRowsAsync();
        Assert.That(rows.All(r => r.JobId == started.Id && r.UserId == boss.Id && r.Username == "boss"), Is.True);
        Assert.That(rows.Select(r => (r.Action, r.Path, r.TargetPath)), Is.EquivalentTo(new (string, string?, string?)[]
        {
            (AuditActions.Ingested, "ingest/gb/a.gb", "roms/gb/T/Tetris.zip"),
            (AuditActions.MovedToDuplicates, "ingest/gb/b.gb", "ingest/gb/_duplicates/b.gb"),
            (AuditActions.MovedToFailed, "ingest/gb/readme.txt", "ingest/gb/_failed/readme.txt"),
            (AuditActions.Ingested, "ingest/gb/pack.zip › nested/sea.gb", "roms/gb/S/seaQuest DSV.zip"),
            (AuditActions.ArchiveDeleted, "ingest/gb/pack.zip", null),
        }));
        Assert.That(rows.Single(r => r.Action == AuditActions.MovedToFailed).Details, Is.EqualTo(IngestFailures.UnsupportedType));
        Assert.That(File.ReadAllText(Path.Combine(Ingest("gb"), "_failed", "readme.txt.error.txt")), Does.Contain($"job: {started.Id}"));
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
        Assert.That((await AuditRowsAsync()).Select(r => (r.Action, r.Path)), Is.EqualTo(new[] { (AuditActions.Ingested, (string?)"ingest/gb/a.gb") }),
            "the ZIP was written, so the row is written even though the mirror failed");
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
        Assert.That(errorTxt, Does.Contain("UnsupportedType").And.Contain($"job: {jobId}").And.Contain("time: "));
        Assert.That((await AuditRowsAsync()).Single(), Is.EqualTo(new AuditRow(AuditActions.MovedToFailed, "gb", null, null, null, jobId,
            "ingest/gb/readme.txt", "ingest/gb/_failed/readme.txt", IngestFailures.UnsupportedType)));
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
    public async Task N64File_InTheGbFolder_IsUnsupportedType()
    {
        var result = await IngestAsync(Drop("gb", "kart.n64", (Kart with { ByteOrder = N64ByteOrder.N64 }).Build()));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
    }

    [Test]
    public async Task NesFile_InTheGbFolder_IsUnsupportedType()
    {
        var result = await IngestAsync(Drop("gb", "pioneer.nes", Pioneer));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
    }

    [Test]
    public async Task GbFile_InTheNesFolder_IsUnsupportedType()
    {
        var result = await IngestAsync(Drop("nes", "tetris.gb", TetrisJ));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
    }

    [Test]
    public async Task Nes_DatMatch_IsIngestedWithItsHeader()
    {
        var result = await IngestAsync(Drop("nes", "pioneer.nes", Pioneer));

        Assert.That(result!.Outcome, Is.EqualTo(IngestOutcome.Ingested));
        var rom = ReadZip("nes/P/Pioneer.zip").Roms.Single();
        Assert.That((rom.FileName, rom.Header!["format"]!.GetValue<string>(), rom.Header["mapper"]!.GetValue<int>()),
            Is.EqualTo(("Pioneer (USA).nes", "iNES", 1)));
    }

    [Test]
    public async Task Nes_UnderTheSmallestDatEntry_IsTooSmall()
    {
        var tiny = new byte[4111];
        "NES"u8.CopyTo(tiny);

        var result = await IngestAsync(Drop("nes", "tiny.nes", tiny));

        Assert.That(result!.Reason, Is.EqualTo(IngestFailures.TooSmall));
    }

    [Test]
    public async Task Nes_WithoutMagic_AndNoDatMatch_IsInvalidHeader()
    {
        var result = await IngestAsync(Drop("nes", "broken.nes", new SyntheticNesRomBuilder { Seed = 71, CorruptMagic = true }.Build()));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.InvalidHeader)));
    }

    [Test]
    public async Task Nes_HeaderlessBytesRenamedNes_AreNotMatchedToTheUnhEntry_InvalidHeader()
    {
        var result = await IngestAsync(Drop("nes", "headerless.nes", PioneerHeaderless));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.InvalidHeader)));
    }

    [Test]
    public async Task N64_SameRomInTwoByteOrders_IsIngestedAsZ64ThenDuplicate()
    {
        var first = await IngestAsync(Drop("n64", "kart.z64", Kart.Build()));
        var second = await IngestAsync(Drop("n64", "kart.n64", (Kart with { ByteOrder = N64ByteOrder.N64 }).Build()));

        Assert.That((first!.Outcome, second!.Outcome), Is.EqualTo((IngestOutcome.Ingested, IngestOutcome.Duplicate)));
        Assert.That(second.Sha1, Is.EqualTo(first.Sha1));
        Assert.That(ReadZip("n64/K/Kart 64.zip").Roms.Single().FileName, Is.EqualTo("Kart 64 (USA).z64"));
    }

    [Test]
    public async Task SfcFile_InTheNesFolder_IsUnsupportedType()
    {
        var result = await IngestAsync(Drop("nes", "rally.sfc", Rally.Build()));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
    }

    [Test]
    public async Task NesFile_InTheSnesFolder_IsUnsupportedType()
    {
        var result = await IngestAsync(Drop("snes", "pioneer.nes", Pioneer));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
    }

    [Test]
    public async Task Snes_HeaderlessThenCopierHeadered_IsOneGameThenDuplicate()
    {
        var first = await IngestAsync(Drop("snes", "rally.sfc", Rally.Build()));
        var second = await IngestAsync(Drop("snes", "rally.smc", (Rally with { CopierHeader = true }).Build()));

        Assert.That((first!.Outcome, second!.Outcome), Is.EqualTo((IngestOutcome.Ingested, IngestOutcome.Duplicate)));
        Assert.That(second.Sha1, Is.EqualTo(first.Sha1));
        var rom = ReadZip("snes/M/Mode Seven Rally.zip").Roms.Single();
        Assert.That((rom.FileName, rom.Header!["mapping"]!.GetValue<string>()), Is.EqualTo(("Mode Seven Rally (USA).sfc", "LoROM")));
    }

    [Test]
    public async Task Snes_TooSmall_And_TooLarge_Fail()
    {
        var small = await IngestAsync(Drop("snes", "tiny.sfc", new byte[8191]));
        var large = await IngestAsync(Drop("snes", "huge.smc", [1]) with { SizeBytes = 8L * 1024 * 1024 + 513 });

        Assert.That((small!.Reason, large!.Reason), Is.EqualTo((IngestFailures.TooSmall, IngestFailures.TooLarge)));
    }

    [Test]
    public async Task Snes_Unmatched_NoValidChecksumAndANonPrintableTitle_IsInvalidHeader()
    {
        var rom = new SyntheticSnesRomBuilder { Seed = 81, SizeKiB = 64, ValidChecksum = false, Title = "BAD\u0001TITLE" }.Build();

        var result = await IngestAsync(Drop("snes", "bad.sfc", rom));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.InvalidHeader)));
    }

    [Test]
    public async Task Snes_Unmatched_NoValidChecksumAndAPrintableTitle_WaitsInReview_SuggestedFromTheTitle()
    {
        var rom = new SyntheticSnesRomBuilder { Seed = 82, SizeKiB = 64, ValidChecksum = false, Title = "MODE SEVEN RALLY" }.Build();

        var result = await IngestAsync(Drop("snes", "unknown.sfc", rom));

        Assert.That((result!.Outcome, result.Reason, result.SystemId), Is.EqualTo((IngestOutcome.Review, "NoDatMatch", "snes")));
        var item = (await Service<IIngestReviewRepository>().GetBySha1Async(result.Sha1!, default))!;
        Assert.That(item.Suggestions, Does.Contain("Mode Seven Rally"));
    }

    [Test]
    public async Task Snes_Unmatched_TooShortForAnyHeader_WaitsInReview()
    {
        var rom = new byte[16 * 1024];
        new Random(83).NextBytes(rom);

        var result = await IngestAsync(Drop("snes", "short.sfc", rom));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Review, "NoDatMatch")));
    }

    [Test]
    public async Task Snes_UnmatchedCopierHeaderedSmc_ResolvedInReview_IsStoredAsSfcWithTheStrippedBytes()
    {
        var hack = new SyntheticSnesRomBuilder { Seed = 84, Title = "RALLY HACK" };
        var stripped = Convert.ToHexStringLower(SHA1.HashData(hack.Build()));
        var result = await IngestAsync(Drop("snes", "hack.smc", (hack with { CopierHeader = true }).Build()));
        Assert.That((result!.Outcome, result.Sha1), Is.EqualTo((IngestOutcome.Review, stripped)));
        var user = await TestUsers.CreateAsync(_factory, "snes-resolver", TestUsers.AllRoles);
        var item = (await Service<IIngestReviewRepository>().GetBySha1Async(stripped, default))!;

        var resolved = await Service<ReviewResolver>().ResolveAsync(item.Id,
            new ReviewResolutionRequest { Action = "Manual", Title = "Rally Hack", Regions = ["USA"], Languages = ["En"], Flags = [] },
            new AuditActor(user.Id, user.Username, null), default);

        Assert.That(resolved.Code, Is.Null, string.Join("; ", resolved.Errors?.SelectMany(e => e.Value) ?? []));
        var row = (await Service<IGameRepository>().GetAsync(resolved.GameId!.Value, default))!;
        var rom = ReadZip(row.ZipPath).Roms.Single();
        Assert.That((Path.GetExtension(rom.FileName), rom.Metadata.Sha1, rom.OriginalFileName), Is.EqualTo((".sfc", stripped, "hack.smc")));
    }

    private static byte[] CopierHeadered(byte[] rom) => [.. new byte[512], .. rom];

    [TestCase("md", "circuit.pce")]
    [TestCase("tg16", "comet.md")]
    public async Task TurboGrafx16_FileInAnotherFamilysFolder_IsUnsupportedType(string folder, string name)
    {
        var result = await IngestAsync(Drop(folder, name, name.EndsWith(".pce") ? Circuit : Comet));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
    }

    [TestCase(73727, "TooSmall")]
    [TestCase(2621953, "TooLarge")]
    public async Task TurboGrafx16_OutsideTheSizeLimits_Fails(int size, string reason)
    {
        var result = await IngestAsync(Drop("tg16", "odd.pce", Atari(size, 181)));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, reason)));
    }

    [Test]
    public async Task TurboGrafx16_Unmatched_GoesToReview_WithoutHeader_AndManualIsStoredAsPce()
    {
        var item = await ReviewAsync("tg16", "Neon Circuit Hack.pce", Atari(262144, 182));
        Assert.That(item.Header, Is.Null);

        var rom = await ResolveGameGearAsync(item, GameGearManual("Neon Circuit Hack"));

        Assert.That((rom.FileName, rom.OriginalFileName), Is.EqualTo(("Neon Circuit Hack (World).pce", "Neon Circuit Hack.pce")));
    }

    [Test]
    public async Task TurboGrafx16_HeaderlessThenCopierHeadered_IsOneGameThenDuplicate()
    {
        var first = await IngestAsync(Drop("tg16", "circuit.pce", Circuit));
        var second = await IngestAsync(Drop("tg16", "circuit-headered.pce", CopierHeadered(Circuit)));

        Assert.That((first!.Outcome, second!.Outcome), Is.EqualTo((IngestOutcome.Ingested, IngestOutcome.Duplicate)));
        Assert.That(second.Sha1, Is.EqualTo(first.Sha1));
        var game = ReadZip(Path.Combine("tg16", "N", "Neon Circuit.zip"));
        Assert.That((game.System, game.Roms.Single().FileName), Is.EqualTo(("tg16", "Neon Circuit (USA).pce")));
        Assert.That(game.Roms.Single().Header, Is.Null);
    }

    [Test]
    public async Task TurboGrafx16_UnmatchedCopierHeadered_ResolvedInReview_IsStoredAsPceWithTheStrippedBytes()
    {
        var hack = Atari(262144, 183);
        var stripped = Convert.ToHexStringLower(SHA1.HashData(hack));
        var result = await IngestAsync(Drop("tg16", "hack.pce", CopierHeadered(hack)));
        Assert.That((result!.Outcome, result.Reason, result.Sha1), Is.EqualTo((IngestOutcome.Review, "NoDatMatch", stripped)));
        var item = (await Service<IIngestReviewRepository>().GetBySha1Async(stripped, default))!;
        Assert.That(item.SizeBytes, Is.EqualTo(262144));

        var rom = await ResolveGameGearAsync(item, GameGearManual("Neon Circuit Hack"));

        Assert.That((rom.FileName, rom.Metadata.Sha1, rom.OriginalFileName), Is.EqualTo(("Neon Circuit Hack (World).pce", stripped, "hack.pce")));
    }

    [TestCase("nes", "lanes.a26")]
    [TestCase("a2600", "pioneer.nes")]
    public async Task Atari_FileInAnotherFamilysFolder_IsUnsupportedType(string folder, string name)
    {
        var result = await IngestAsync(Drop(folder, name, name.EndsWith(".nes") ? Pioneer : Lanes));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
    }

    [TestCase(2047, "TooSmall")]
    [TestCase(65537, "TooLarge")]
    public async Task Atari_OutsideTheSizeLimits_Fails(int size, string reason)
    {
        var result = await IngestAsync(Drop("a2600", "odd.bin", Atari(size, 121)));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, reason)));
    }

    [Test]
    public async Task Atari_UnmatchedBin_GoesToReview_AndResolvedIsStoredAsA26()
    {
        var bytes = Atari(4096, 122);
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes));
        var result = await IngestAsync(Drop("a2600", "Laser Lanes Hack.bin", bytes));
        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Review, "NoDatMatch")));
        var item = (await Service<IIngestReviewRepository>().GetBySha1Async(sha1, default))!;
        Assert.That(item.Header, Is.Null);
        Assert.That(item.Suggestions, Does.Contain("Laser Lanes"));
        var user = await TestUsers.CreateAsync(_factory, "atari-resolver", TestUsers.AllRoles);

        var resolved = await Service<ReviewResolver>().ResolveAsync(item.Id,
            new ReviewResolutionRequest { Action = "Manual", Title = "Laser Lanes Hack", Regions = ["USA"], Languages = ["En"], Flags = [] },
            new AuditActor(user.Id, user.Username, null), default);

        Assert.That(resolved.Code, Is.Null, string.Join("; ", resolved.Errors?.SelectMany(e => e.Value) ?? []));
        var row = (await Service<IGameRepository>().GetAsync(resolved.GameId!.Value, default))!;
        var rom = ReadZip(row.ZipPath).Roms.Single();
        Assert.That((Path.GetExtension(rom.FileName), rom.Metadata.Sha1, rom.OriginalFileName, rom.Header),
            Is.EqualTo((".a26", sha1, "Laser Lanes Hack.bin", (System.Text.Json.Nodes.JsonObject?)null)));
    }

    [Test]
    public async Task Atari_ZipHoldingADatGame_IsIngested()
    {
        var path = Path.Combine(Ingest("a2600"), "lanes.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("Laser Lanes (USA).a26").Open())
            entry.Write(Lanes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        await ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, Substitute.For<IJobItemRepository>())
            .RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);

        Assert.That(File.Exists(path), Is.False, "fully ingested archive is deleted");
        var game = ReadZip(Path.Combine("a2600", "L", "Laser Lanes.zip"));
        Assert.That((game.System, game.Roms.Single().FileName), Is.EqualTo(("a2600", "Laser Lanes (USA).a26")));
    }

    [TestCase("nes", "pursuit.col")]
    [TestCase("coleco", "pioneer.nes")]
    public async Task Coleco_FileInAnotherFamilysFolder_IsUnsupportedType(string folder, string name)
    {
        var result = await IngestAsync(Drop(folder, name, name.EndsWith(".nes") ? Pioneer : Pursuit));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.UnsupportedType)));
    }

    [TestCase(8191, "TooSmall")]
    [TestCase(32769, "TooLarge")]
    public async Task Coleco_OutsideTheSizeLimits_Fails(int size, string reason)
    {
        var result = await IngestAsync(Drop("coleco", "odd.col", Coleco(size, 131, 0xAA, 0x55)));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, reason)));
    }

    [Test]
    public async Task Coleco_UnmatchedBinWithMagic_GoesToReview_AndResolvedIsStoredAsCol()
    {
        var bytes = Coleco(8192, 132, 0x55, 0xAA);
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes));
        var result = await IngestAsync(Drop("coleco", "Pixel Pursuit Hack.bin", bytes));
        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Review, "NoDatMatch")));
        var item = (await Service<IIngestReviewRepository>().GetBySha1Async(sha1, default))!;
        Assert.That(JsonNode.Parse(item.Header!)!["magic"]!.GetValue<string>(), Is.EqualTo("55AA"));
        Assert.That(item.Suggestions, Does.Contain("Pixel Pursuit"));
        var user = await TestUsers.CreateAsync(_factory, "coleco-resolver", TestUsers.AllRoles);

        var resolved = await Service<ReviewResolver>().ResolveAsync(item.Id,
            new ReviewResolutionRequest { Action = "Manual", Title = "Pixel Pursuit Hack", Regions = ["USA"], Languages = ["En"], Flags = [] },
            new AuditActor(user.Id, user.Username, null), default);

        Assert.That(resolved.Code, Is.Null, string.Join("; ", resolved.Errors?.SelectMany(e => e.Value) ?? []));
        var row = (await Service<IGameRepository>().GetAsync(resolved.GameId!.Value, default))!;
        var rom = ReadZip(row.ZipPath).Roms.Single();
        Assert.That((rom.FileName, rom.Metadata.Sha1, rom.OriginalFileName), Is.EqualTo(("Pixel Pursuit Hack (USA).col", sha1, "Pixel Pursuit Hack.bin")));
    }

    [Test]
    public async Task Coleco_UnmatchedBinWithoutMagic_IsInvalidHeader()
    {
        var result = await IngestAsync(Drop("coleco", "junk.bin", Coleco(8192, 133, 0x00, 0x00)));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.InvalidHeader)));
        Assert.That(File.Exists(Path.Combine(Ingest("coleco"), "_failed", "junk.bin")), Is.True);
    }

    [Test]
    public async Task Coleco_ZipHoldingADatGame_IsIngested()
    {
        var path = Path.Combine(Ingest("coleco"), "pursuit.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("Pixel Pursuit (USA).col").Open())
            entry.Write(Pursuit);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        await ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, Substitute.For<IJobItemRepository>())
            .RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);

        Assert.That(File.Exists(path), Is.False, "fully ingested archive is deleted");
        var game = ReadZip(Path.Combine("coleco", "P", "Pixel Pursuit.zip"));
        Assert.That((game.System, game.Roms.Single().FileName), Is.EqualTo(("coleco", "Pixel Pursuit (USA).col")));
    }

    [Test]
    public async Task Snes_ZipHoldingACopierHeaderedSmc_IsIngestedAsTheStrippedSfc()
    {
        var path = Path.Combine(Ingest("snes"), "rally.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("Mode Seven Rally (USA).smc").Open())
            entry.Write((Rally with { CopierHeader = true }).Build());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        await ActivatorUtilities.CreateInstance<IngestJob>(_factory.Services, Substitute.For<IJobItemRepository>())
            .RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), default);

        var sfc = Rally.Build();
        Assert.That(File.Exists(path), Is.False, "fully ingested archive is deleted");
        using var library = LibraryZip.Open(Path.Combine(Roms, "snes", "M", "Mode Seven Rally.zip"), Service<InfoJsonReader>());
        Assert.That(library.InfoJson!.Game!.Roms.Single().FileName, Is.EqualTo("Mode Seven Rally (USA).sfc"));
        using var stored = library.OpenEntry(LibraryZip.RomEntry("Mode Seven Rally (USA).sfc"), sfc.Length);
        using var bytes = new MemoryStream();
        stored.CopyTo(bytes);
        Assert.That(bytes.ToArray(), Is.EqualTo(sfc));
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
        Assert.That((await AuditRowsAsync()).Select(r => (r.Action, r.Path, r.Details)), Is.EqualTo(new[]
        {
            (AuditActions.MovedToFailed, (string?)"ingest/gb/tiny.gb", (string?)IngestFailures.TooSmall),
            (AuditActions.MovedToFailed, "ingest/gb/huge.gb", IngestFailures.TooLarge),
        }));
    }

    [Test]
    public async Task UnmatchedWithInvalidLogoAndChecksum_IsInvalidHeader()
    {
        var garbage = new SyntheticRomBuilder { Seed = 50, CorruptLogo = true, CorruptHeaderChecksum = true }.Build();

        var result = await IngestAsync(Drop("gb", "garbage.gb", garbage));

        Assert.That((result!.Outcome, result.Reason), Is.EqualTo((IngestOutcome.Failed, IngestFailures.InvalidHeader)));
        Assert.That(result.Sha1, Is.EqualTo(Convert.ToHexStringLower(SHA1.HashData(garbage))));
        Assert.That(File.Exists(Path.Combine(Ingest("gb"), "_failed", "garbage.gb")), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(Ingest("gb"), "_failed", "garbage.gb.error.txt")), Does.Contain("the header isn't valid for this system"));
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
        var renamed = (await AuditRowsAsync()).Single();
        Assert.That((renamed.Action, renamed.Path, renamed.TargetPath), Is.EqualTo((AuditActions.ExtensionRenamed, "ingest/gb/mystery.gb", "ingest/gb/mystery.gbc")));
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
        var moved = (await AuditRowsAsync()).Last();
        Assert.That((moved.Action, moved.Path, moved.TargetPath), Is.EqualTo((AuditActions.MovedToDuplicates, "ingest/gb/copy.gb", "ingest/gb/_duplicates/copy.gb")));
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
        var repairedRow = (await AuditRowsAsync()).Last();
        Assert.That((repairedRow.Action, repairedRow.Path, repairedRow.TargetPath, repairedRow.Details),
            Is.EqualTo((AuditActions.ZipRepaired, "ingest/gb/good-copy.gb", "roms/gb/T/Tetris.zip", (string?)null)));
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
