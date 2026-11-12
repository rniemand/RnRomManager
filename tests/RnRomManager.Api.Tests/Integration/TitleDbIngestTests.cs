using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>
/// Spec T4, T5, T7 through ingest, with the test TitleDb handler (<c>extdb</c>) and a compact fixture written before
/// startup. Review resolution of a titledb identity is refused until step 24.
/// </summary>
[Category("Integration")]
public class TitleDbIngestTests
{
    private const string BaseId = "0100000000002000";
    private const string UpdateId = "0100000000002800";
    private const string DlcId = "0100000000003001";
    private const string NamedDlcId = "0100000000003002";
    private const string OddKindId = "0100000000002400";
    private const string OrphanDlcId = "0100000000009001";
    private const string CometBaseId = "0100000000006000";
    private const string CometUpdateId = "0100000000006800";

    private ApiFactory? _factory;

    private ApiFactory Factory => _factory ?? throw new InvalidOperationException("Start() first");

    private const string Cdn = "https://img-eshop.cdn.nintendo.net/i/";

    /// <summary>
    /// A host with the extdb handler, its header carrying <paramref name="headerTitleId"/> when set. With
    /// <paramref name="cdn"/>, media is on and the titledb image client is served by it (the base entry has a box art
    /// and a snap URL); otherwise media is off.
    /// </summary>
    private async Task StartAsync(string? headerTitleId = null, HttpMessageHandler? cdn = null)
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services =>
            {
                services.AddSingleton<ISystemHandler>(new ExternalTitleDbTestHandler(headerTitleId));
                services.AddSingleton<ISystemRegistry, SystemRegistry>();
                if (cdn is not null)
                    services.AddHttpClient(RnRomManager.Api.Http.HttpClients.TitleDbImages).ConfigurePrimaryHttpMessageHandler(() => cdn);
            },
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = cdn is null ? "" : "https://thumbs.test"; // empty: default art, no network
        FixtureDatWriter.Write(Path.Combine(_factory.Root, "cache", DatCache.FolderName), new Dictionary<string, IReadOnlyList<FixtureGame>>(),
            titleDb: new Dictionary<string, IReadOnlyList<TitleDbRow>>
            {
                [ExternalTitleDbTestHandler.DatNameValue] =
                [
                    new(BaseId, "Orbit Expedition", "Orbit Co", null, 2020, ["Action"], ["en"], cdn is null ? null : Cdn + "box.jpg",
                        cdn is null ? null : Cdn + "snap.jpg"),
                    new(NamedDlcId, "Orbit Expedition: Star Pack", "Orbit Co", null, 2021, [], ["ja"], null, null),
                    new(OddKindId, "Orbit Oddity", null, null, null, [], [], null, null),
                    new(OrphanDlcId, "Lost Pack", null, null, null, [], [], null, null),
                    new(CometBaseId, "Comet Run", null, null, null, [], ["en"], null, null),
                    new(CometUpdateId, "Comet Run Update", null, null, null, [], ["en"], null, null),
                ],
            });
        _ = _factory.Services; // start: folders and the DAT load
    }

    [TearDown]
    public void TearDown() => _factory?.Dispose();

    private T Service<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

    private string Ingest => Path.Combine(Factory.Root, "ingest", ExternalTitleDbTestHandler.SystemId);
    private string Bucket => Path.Combine(Factory.Root, "roms", ExternalTitleDbTestHandler.SystemId, "O");
    private string ZipPath => Path.Combine(Bucket, "Orbit Expedition.zip");

    private static byte[] Bytes(byte seed, int length = 2048)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        bytes[0] = 0x00; // a plausible header
        return bytes;
    }

    private IngestFile Drop(string name, byte[] bytes)
    {
        var path = Path.Combine(Ingest, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var info = new FileInfo(path);
        return new IngestFile(ExternalTitleDbTestHandler.SystemId, path, name, info.Length, info.LastWriteTimeUtc);
    }

    private Task<IngestItemResult?> IngestAsync(IngestFile file) =>
        Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new AuditActor(null, null, Guid.NewGuid()), new CircuitBreaker(), CancellationToken.None);

    private static (string?, string?) Code(IngestItemResult? result) => (result?.Outcome, result?.Reason);

    private InfoJsonGame ReadGame(string? zip = null)
    {
        using var library = LibraryZip.Open(zip ?? ZipPath, Service<InfoJsonReader>());
        return library.InfoJson!.Game!;
    }

    private async Task<ReviewItem> ReviewOfAsync(byte[] bytes) =>
        (await Service<IIngestReviewRepository>().GetBySha1Async(Convert.ToHexStringLower(SHA1.HashData(bytes)), default))!;

    private async Task<AuditActor> AdminAsync()
    {
        var name = "boss" + Guid.NewGuid().ToString("N")[..8];
        var user = await TestUsers.CreateAsync(Factory, name, Roles.Of(Roles.Admin));
        return new AuditActor(user.Id, name, null);
    }

    private ReviewResolver Resolver() => Service<ReviewResolver>();

    private static readonly byte[] BaseRom = Bytes(1);
    private static readonly byte[] UpdateRom = Bytes(2);
    private static readonly byte[] DlcRom = Bytes(3);

    private async Task<Guid> IngestBaseAsync()
    {
        var result = await IngestAsync(Drop($"orbit [{BaseId}][v0].nsp", BaseRom));
        Assert.That(Code(result), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        return result!.GameId!.Value;
    }

    [Test]
    public async Task UpdateAndDlc_JoinTheBaseGame_WithFlags()
    {
        await StartAsync();
        var gameId = await IngestBaseAsync();

        Assert.That(Code(await IngestAsync(Drop($"orbit [{UpdateId}][v65536].NSP", UpdateRom))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        Assert.That(Code(await IngestAsync(Drop($"orbit dlc [{DlcId}].xci", DlcRom))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));

        var game = ReadGame();
        Assert.That(game.GameId, Is.EqualTo(gameId));
        Assert.That(game.Title, Is.EqualTo("Orbit Expedition"));
        Assert.That(game.Roms.Select(r => (r.FileName, r.Revision, string.Join(",", r.Flags))), Is.EquivalentTo(new[]
        {
            ($"Orbit Expedition [{BaseId}][v0].nsp", (string?)null, ""),
            ($"Orbit Expedition [{UpdateId}][v65536].nsp", "v65536", "Update"),
            ($"Orbit Expedition (DLC) [{DlcId}][v0].xci", null, "DLC"),
        }));
        Assert.That(game.Roms.Select(r => r.Identification), Is.All.EqualTo(
            new InfoJsonIdentification(IdentificationMethod.TitleDb, DatStore.TitleDbName, FixtureDatWriter.TitleDbVersion, $"Orbit Expedition [{BaseId}]")));
        Assert.That(game.Roms.Select(r => r.Languages), Is.All.EqualTo(new[] { "En" }));
        Assert.That((game.ReleaseYear, game.Publishers, game.Genres, game.Developers),
            Is.EqualTo(((int?)2020, (IReadOnlyList<string>)["Orbit Co"], (IReadOnlyList<string>)["Action"], (IReadOnlyList<string>)[])));

        // External: the files sit beside the ZIP, which has no ROM entries; the library mirrors all three.
        foreach (var rom in game.Roms)
            Assert.That(File.Exists(Path.Combine(Bucket, rom.ExternalFileName!)), Is.True, rom.ExternalFileName);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(ZipPath))
            Assert.That(zip.Entries.Any(e => e.FullName.StartsWith("roms/")), Is.False);
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM roms WHERE game_id = @id", new { id = gameId.ToString() }), Is.EqualTo(3));
    }

    [Test]
    public async Task Updates_IngestWithTheirVersion_DistinctNames_AndPointersWithoutTheRepeatedTitle()
    {
        await StartAsync();
        var gameId = await IngestBaseAsync();

        Assert.That(Code(await IngestAsync(Drop($"orbit [{UpdateId}][v0][1.0.1].nsp", UpdateRom))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        Assert.That(Code(await IngestAsync(Drop($"orbit [{UpdateId}][v0][1.1.0].nsp", DlcRom))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));

        var game = ReadGame();
        Assert.That(game.Roms.Select(r => (r.FileName, r.ExternalFileName, r.Version)), Is.EquivalentTo(new[]
        {
            ($"Orbit Expedition [{BaseId}][v0].nsp", (string?)$"Orbit Expedition.[{BaseId}][v0].nsp", (string?)null),
            ($"Orbit Expedition [{UpdateId}][v0][1.0.1].nsp", $"Orbit Expedition.[{UpdateId}][v0][1.0.1].nsp", "1.0.1"),
            ($"Orbit Expedition [{UpdateId}][v0][1.1.0].nsp", $"Orbit Expedition.[{UpdateId}][v0][1.1.0].nsp", "1.1.0"),
        }));
        foreach (var rom in game.Roms)
            Assert.That(File.Exists(Path.Combine(Bucket, rom.ExternalFileName!)), Is.True, rom.ExternalFileName);
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QueryAsync<string?>("SELECT version FROM roms WHERE game_id = @id ORDER BY version", new { id = gameId.ToString() }),
            Is.EqualTo(new[] { null, "1.0.1", "1.1.0" }));
    }

    [Test]
    public async Task UpdMarker_OnABaseIdFromTheFileName_IngestsAsItsUpdate_B7()
    {
        await StartAsync();
        await IngestBaseAsync();

        Assert.That(Code(await IngestAsync(Drop($"orbit [{BaseId}][upd][1.2.0].nsp", UpdateRom))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));

        var update = ReadGame().Roms.Single(r => r.Flags.Contains("Update"));
        Assert.That((update.FileName, update.Version, update.OriginalFileName),
            Is.EqualTo(($"Orbit Expedition [{UpdateId}][v0][1.2.0].nsp", (string?)"1.2.0", $"orbit [{BaseId}][upd][1.2.0].nsp")));
    }

    [Test]
    public async Task UpdMarker_IsIgnored_WhenTheHeaderHasTheTitleId_B7()
    {
        await StartAsync(headerTitleId: BaseId);

        Assert.That(Code(await IngestAsync(Drop("orbit [UPD][1.2.0].nsp", BaseRom))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));

        var rom = ReadGame().Roms.Single();
        Assert.That((rom.FileName, rom.Flags.Count, rom.Version), Is.EqualTo(($"Orbit Expedition [{BaseId}][v0].nsp", 0, (string?)null)));
    }

    [TestCase("0100000000002400", TestName = "NoMatch_GoesToReviewWithSuggestions unsupported kind")]
    [TestCase("0100000000004000", TestName = "NoMatch_GoesToReviewWithSuggestions missing base")]
    public async Task NoMatch_GoesToReviewWithSuggestions(string titleId)
    {
        await StartAsync();
        var bytes = Bytes(9);

        Assert.That(Code(await IngestAsync(Drop($"Orbit Expedition [{titleId}].nsp", bytes))), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));

        var item = await ReviewOfAsync(bytes);
        Assert.That(item.Suggestions, Does.Contain($"Orbit Expedition [{BaseId}]"));
    }

    [Test]
    public async Task UnmatchedImplausibleHeader_IsInvalidHeader()
    {
        await StartAsync();
        var bytes = Bytes(9);
        bytes[0] = 0xFF;

        Assert.That(Code(await IngestAsync(Drop("mystery.nsp", bytes))), Is.EqualTo((IngestOutcome.Failed, (string?)IngestFailures.InvalidHeader)));
    }

    [Test]
    public async Task ASwitch2HeaderId_QueuesNoDatMatch_EvenWithAValidIdInTheName()
    {
        await StartAsync(headerTitleId: "0400000000002000");
        var bytes = Bytes(9);

        Assert.That(Code(await IngestAsync(Drop($"orbit [{BaseId}].nsp", bytes))), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
    }

    [Test]
    public async Task UnmatchedXci_KeepsItsExtensionInReview()
    {
        await StartAsync();
        var bytes = Bytes(9);
        var file = Drop("mystery.xci", bytes);

        Assert.That(Code(await IngestAsync(file)), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));

        Assert.That(File.Exists(file.FullPath), Is.True);
        Assert.That((await ReviewOfAsync(bytes)).SourcePath, Is.EqualTo(file.FullPath));
    }

    [Test]
    public async Task ExistingGame_FillsOnlyItsEmptyMetadata()
    {
        await StartAsync();
        var mystery = Bytes(9);
        Assert.That(Code(await IngestAsync(Drop("mystery.nsp", mystery))), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
        var manual = new ReviewResolutionRequest { Action = "Manual", Title = "Orbit Expedition", Regions = [], Languages = [], Flags = [] };
        Assert.That((await Resolver().ResolveAsync((await ReviewOfAsync(mystery)).Id, manual, await AdminAsync(), default)).Code, Is.Null);
        Assert.That(ReadGame().ReleaseYear, Is.Null);

        await IngestBaseAsync();

        var game = ReadGame();
        Assert.That(game.Roms, Has.Count.EqualTo(2), "joined the existing game");
        Assert.That((game.ReleaseYear, game.Publishers), Is.EqualTo(((int?)2020, (IReadOnlyList<string>)["Orbit Co"])));
    }

    [Test]
    public async Task MoveReleaseToANewGame_TakesTheBaseEntrysMetadata()
    {
        await StartAsync();
        var gameId = await IngestBaseAsync();
        Assert.That(Code(await IngestAsync(Drop($"orbit [{UpdateId}].nsp", UpdateRom))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        long romId;
        await using (var db = Service<IDbConnectionFactory>().Create())
            romId = await db.QuerySingleAsync<long>("SELECT id FROM roms WHERE sha1 = @sha1", new { sha1 = Convert.ToHexStringLower(SHA1.HashData(UpdateRom)) });

        var moved = await Service<LibraryEditor>().MoveReleaseAsync(gameId, romId, null, "Orbit Updates", await AdminAsync(), default);

        Assert.That(moved.Code, Is.Null);
        var game = ReadGame(Path.Combine(Bucket, "Orbit Updates.zip"));
        Assert.That((game.ReleaseYear, game.Publishers, game.Genres), Is.EqualTo(((int?)2020, (IReadOnlyList<string>)["Orbit Co"], (IReadOnlyList<string>)["Action"])));
    }

    /// <summary>Two games own the base title "Orbit Expedition", so a matching update waits as AmbiguousGroup.</summary>
    private async Task<(ReviewItem Item, IngestFile File, Guid First)> AmbiguousAsync(string updateName = $"orbit [{UpdateId}][v3].nsp")
    {
        var first = await IngestBaseAsync();
        var other = Bytes(9);
        Assert.That(Code(await IngestAsync(Drop("other.nsp", other))), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
        var manual = new ReviewResolutionRequest { Action = "Manual", Title = "Another Game", Regions = [], Languages = [], Flags = [] };
        var second = (await Resolver().ResolveAsync((await ReviewOfAsync(other)).Id, manual, await AdminAsync(), default)).GameId!.Value;
        await using (var db = Service<IDbConnectionFactory>().Create())
            await db.ExecuteAsync("UPDATE roms SET base_title = 'Orbit Expedition' WHERE game_id = @id", new { id = second.ToString() });

        var file = Drop(updateName, UpdateRom);
        Assert.That(Code(await IngestAsync(file)), Is.EqualTo((IngestOutcome.Review, (string?)"AmbiguousGroup")));
        return (await ReviewOfAsync(UpdateRom), file, first);
    }

    [Test]
    public async Task AmbiguousGroup_KeepsTheWholeTitleDbIdentity()
    {
        await StartAsync();
        var (item, _, _) = await AmbiguousAsync();

        var queued = JsonDocument.Parse(item.DatIdentification!).RootElement;
        Assert.That(queued.GetProperty("datName").GetString(), Is.EqualTo(DatStore.TitleDbName));
        Assert.That(queued.GetProperty("datVersion").GetString(), Is.EqualTo(FixtureDatWriter.TitleDbVersion));
        Assert.That(queued.GetProperty("datGameName").GetString(), Is.EqualTo($"Orbit Expedition [{BaseId}]"));
        Assert.That(queued.GetProperty("romName").GetString(), Is.EqualTo($"Orbit Expedition [{UpdateId}][v3].nsp"));
        Assert.That(queued.GetProperty("titleId").GetString(), Is.EqualTo(UpdateId));
        Assert.That(queued.GetProperty("baseTitle").GetString(), Is.EqualTo("Orbit Expedition"));
        Assert.That(queued.GetProperty("regions").GetArrayLength(), Is.Zero);
        Assert.That(queued.GetProperty("languages").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "En" }));
        Assert.That(queued.GetProperty("revision").GetString(), Is.EqualTo("v3"));
        Assert.That(queued.GetProperty("flags").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "Update" }));
    }

    // --- T9 / T4: Review resolution of a titledb identity ---

    /// <summary>A refresh that drops every titledb entry, with a newer version.</summary>
    private void RefreshWithoutEntries()
    {
        FixtureDatWriter.Write(Path.Combine(Factory.Root, "cache", DatCache.FolderName), new Dictionary<string, IReadOnlyList<FixtureGame>>(),
            titleDb: new Dictionary<string, IReadOnlyList<TitleDbRow>> { [ExternalTitleDbTestHandler.DatNameValue] = [] }, titleDbVersion: "2026.02.02");
        Service<DatCache>().Load();
        Assert.That(Service<DatCache>().For(ExternalTitleDbTestHandler.SystemId)!.Roms, Is.Empty);
    }

    /// <summary>An unmatched loose file waiting in Review as NoDatMatch.</summary>
    private async Task<(ReviewItem Item, IngestFile File)> QueueAsync(string name, byte seed = 9)
    {
        var bytes = Bytes(seed);
        var file = Drop(name, bytes);
        Assert.That(Code(await IngestAsync(file)), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
        return (await ReviewOfAsync(bytes), file);
    }

    private static ReviewResolutionRequest Dat(string datGameName) =>
        new() { Action = "Dat", DatSystem = ExternalTitleDbTestHandler.SystemId, DatGameName = datGameName };

    [Test]
    public async Task DatPick_StoresTheRelease_AsTitleDb_AndMovesTheFile()
    {
        await StartAsync();
        var (item, file) = await QueueAsync("mystery [v2].XCI");

        var result = await Resolver().ResolveAsync(item.Id, Dat($"Orbit Expedition [{BaseId}]"), await AdminAsync(), default);

        Assert.That(result.Code, Is.Null);
        var rom = ReadGame().Roms.Single();
        Assert.That((rom.FileName, rom.Revision, rom.Flags.Count), Is.EqualTo(($"Orbit Expedition [{BaseId}][v2].xci", (string?)"v2", 0)),
            "no Unverified flag or name part; N from the original name; its extension kept");
        Assert.That(rom.Identification, Is.EqualTo(
            new InfoJsonIdentification(IdentificationMethod.TitleDb, DatStore.TitleDbName, FixtureDatWriter.TitleDbVersion, $"Orbit Expedition [{BaseId}]")));
        Assert.That(ReadGame().ReleaseYear, Is.EqualTo(2020));
        Assert.That(File.Exists(file.FullPath), Is.False);
        Assert.That(File.Exists(Path.Combine(Bucket, rom.ExternalFileName!)), Is.True);
        Assert.That((await ReviewOfAsync(Bytes(9))).Status, Is.EqualTo("Resolved"));
    }

    [Test]
    public async Task DatPickOfADlc_GroupsWithItsBase_WithTheDlcFlag()
    {
        await StartAsync();
        var gameId = await IngestBaseAsync();
        var (item, _) = await QueueAsync("pack.nsp");

        var result = await Resolver().ResolveAsync(item.Id, Dat($"Orbit Expedition - Star Pack [{NamedDlcId}]"), await AdminAsync(), default);

        Assert.That((result.Code, result.GameId), Is.EqualTo(((string?)null, (Guid?)gameId)));
        var dlc = ReadGame().Roms.Single(r => r.Flags.Contains("DLC"));
        Assert.That((dlc.FileName, dlc.IsPrimary), Is.EqualTo(($"Orbit Expedition - Star Pack [{NamedDlcId}][v0].nsp", false)));
        Assert.That(dlc.Languages, Is.EqualTo(new[] { "Ja" }));
        Assert.That(dlc.Identification.DatGameName, Is.EqualTo($"Orbit Expedition [{BaseId}]"), "the base's datGameName");
    }

    private static ReviewResolutionRequest UpdatePick(string datGameName) => Dat(datGameName) with { Update = true };

    [Test]
    public async Task UpdatePick_OfABase_StoresItsUpdate_WithVersionAndName_B8()
    {
        await StartAsync();
        var gameId = await IngestBaseAsync();
        var (item, _) = await QueueAsync("DREDGE[UPD][1.0.1].nsp");

        var result = await Resolver().ResolveAsync(item.Id, UpdatePick($"Orbit Expedition [{BaseId}]"), await AdminAsync(), default);

        Assert.That((result.Code, result.GameId), Is.EqualTo(((string?)null, (Guid?)gameId)));
        var update = ReadGame().Roms.Single(r => r.Flags.Contains("Update"));
        Assert.That((update.FileName, update.Version, update.IsPrimary),
            Is.EqualTo(($"Orbit Expedition [{UpdateId}][v0][1.0.1].nsp", (string?)"1.0.1", false)));
        Assert.That(update.Identification.DatGameName, Is.EqualTo($"Orbit Expedition [{BaseId}]"));
    }

    [Test]
    public async Task UpdatePick_OfAnUpdateEntry_StoresThatUpdate_B8()
    {
        await StartAsync();
        var (item, _) = await QueueAsync("comet [2.0].nsp");

        var result = await Resolver().ResolveAsync(item.Id, UpdatePick($"Comet Run Update [{CometUpdateId}]"), await AdminAsync(), default);

        Assert.That(result.Code, Is.Null, string.Join("; ", result.Errors?.Select(e => e.Key + ": " + string.Join(",", e.Value)) ?? []));
        var rom = ReadGame(Path.Combine(Factory.Root, "roms", ExternalTitleDbTestHandler.SystemId, "C", "Comet Run.zip")).Roms.Single();
        Assert.That((rom.FileName, rom.Version, string.Join(",", rom.Flags)),
            Is.EqualTo(($"Comet Run Update [{CometUpdateId}][v0][2.0].nsp", (string?)"2.0", "Update")));
        Assert.That(rom.Identification.DatGameName, Is.EqualTo($"Comet Run [{CometBaseId}]"));
    }

    [TestCase("dlc", TestName = "BadUpdatePick_IsAnUpdateError_AndKeepsPending DLC pick")]
    [TestCase("action", TestName = "BadUpdatePick_IsAnUpdateError_AndKeepsPending non-Dat action")]
    public async Task BadUpdatePick_IsAnUpdateError_AndKeepsPending(string problem)
    {
        await StartAsync();
        var gameId = await IngestBaseAsync();
        var (item, file) = await QueueAsync("mystery [UPD].nsp");
        var request = problem == "dlc"
            ? UpdatePick($"Orbit Expedition - Star Pack [{NamedDlcId}]")
            : new ReviewResolutionRequest { Action = "Attach", TargetGameId = gameId, Flag = "Hack", Update = true };

        var result = await Resolver().ResolveAsync(item.Id, request, await AdminAsync(), default);

        Assert.That((result.Code, result.Errors?.Keys.Single()), Is.EqualTo(("ValidationFailed", (string?)"update")));
        Assert.That((await ReviewOfAsync(Bytes(9))).Status, Is.EqualTo("Pending"));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Bytes(9)));
    }

    [TestCase("kind", TestName = "BadDatPick_IsFieldValidation unsupported kind")]
    [TestCase("base", TestName = "BadDatPick_IsFieldValidation missing base")]
    [TestCase("name", TestName = "BadDatPick_IsFieldValidation not a loaded name")]
    [TestCase("store", TestName = "BadDatPick_IsFieldValidation no store")]
    public async Task BadDatPick_IsFieldValidation(string problem)
    {
        await StartAsync();
        var (item, file) = await QueueAsync("mystery.nsp");
        var pick = problem switch
        {
            "kind" => $"Orbit Oddity [{OddKindId}]",
            "base" => $"Lost Pack [{OrphanDlcId}]",
            _ => $"Orbit Expedition [{BaseId}]",
        };
        if (problem == "name")
            pick = $"Orbit [{BaseId}]";
        if (problem == "store")
        {
            File.Delete(Path.Combine(Factory.Root, "cache", DatCache.FolderName, DatManifest.FileName));
            Service<DatCache>().Load();
        }

        var result = await Resolver().ResolveAsync(item.Id, Dat(pick), await AdminAsync(), default);

        Assert.That((result.Code, result.Errors?.Keys.Single()), Is.EqualTo(("ValidationFailed", (string?)"datGameName")));
        Assert.That((await ReviewOfAsync(Bytes(9))).Status, Is.EqualTo("Pending"));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(Bytes(9)));
    }

    [Test]
    public async Task Manual_KeepsTheXciExtension()
    {
        await StartAsync();
        var (item, _) = await QueueAsync("mystery.XCI");
        var manual = new ReviewResolutionRequest { Action = "Manual", Title = "Mystery Game", Regions = [], Languages = [], Flags = [] };

        Assert.That((await Resolver().ResolveAsync(item.Id, manual, await AdminAsync(), default)).Code, Is.Null);

        var rom = ReadGame(Path.Combine(Factory.Root, "roms", ExternalTitleDbTestHandler.SystemId, "M", "Mystery Game.zip")).Roms.Single();
        Assert.That((rom.FileName, rom.Identification.Method), Is.EqualTo(("Mystery Game.xci", IdentificationMethod.Manual)));
    }

    [Test]
    public async Task FlaggedAttach_KeepsTheXciExtension()
    {
        await StartAsync();
        var gameId = await IngestBaseAsync();
        var (item, _) = await QueueAsync("mystery.xci");
        var attach = new ReviewResolutionRequest { Action = "Attach", TargetGameId = gameId, Flag = "Hack" };

        Assert.That((await Resolver().ResolveAsync(item.Id, attach, await AdminAsync(), default)).Code, Is.Null);

        Assert.That(ReadGame().Roms.Select(r => r.FileName), Does.Contain("mystery (Hack).xci"));
    }

    [Test]
    public async Task QueuedAttach_KeepsTheWholeIdentity_AfterARefreshDropsTheEntry()
    {
        await StartAsync();
        var (item, file, first) = await AmbiguousAsync();
        RefreshWithoutEntries();

        var result = await Resolver().ResolveAsync(item.Id, new ReviewResolutionRequest { Action = "Attach", TargetGameId = first, Flag = "None" },
            await AdminAsync(), default);

        Assert.That(result.Code, Is.Null);
        var rom = ReadGame().Roms.Single(r => r.Flags.Contains("Update"));
        Assert.That((rom.FileName, rom.BaseTitle, rom.Revision), Is.EqualTo(($"Orbit Expedition [{UpdateId}][v3].nsp", "Orbit Expedition", (string?)"v3")));
        Assert.That(rom.Languages, Is.EqualTo(new[] { "En" }));
        Assert.That(rom.Identification, Is.EqualTo(
            new InfoJsonIdentification(IdentificationMethod.TitleDb, DatStore.TitleDbName, FixtureDatWriter.TitleDbVersion, $"Orbit Expedition [{BaseId}]")),
            "the queued DAT version, not the refreshed one");
        Assert.That(File.Exists(file.FullPath), Is.False);
    }

    [Test]
    public async Task QueuedAttach_OfAnUpdate_SetsItsVersion()
    {
        await StartAsync();
        var (item, _, first) = await AmbiguousAsync($"orbit [{UpdateId}][v3][2.0.1].nsp");

        var result = await Resolver().ResolveAsync(item.Id, new ReviewResolutionRequest { Action = "Attach", TargetGameId = first, Flag = "None" },
            await AdminAsync(), default);

        Assert.That(result.Code, Is.Null);
        var rom = ReadGame().Roms.Single(r => r.Flags.Contains("Update"));
        Assert.That((rom.FileName, rom.Version, rom.ExternalFileName),
            Is.EqualTo(($"Orbit Expedition [{UpdateId}][v3][2.0.1].nsp", (string?)"2.0.1", (string?)$"Orbit Expedition.[{UpdateId}][v3][2.0.1].nsp")));
    }

    [Test]
    public async Task ArchivedDatRetry_ResolvesTheCommittedRelease_AfterARefreshDropsTheEntry()
    {
        await StartAsync();
        var bytes = Bytes(9);
        var archive = Path.Combine(Ingest, "pack.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("dump.nsp").Open())
            entry.Write(bytes);
        File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddMinutes(-1));
        await ActivatorUtilities.CreateInstance<IngestJob>(Factory.Services, NSubstitute.Substitute.For<IJobItemRepository>()).RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), CancellationToken.None);
        var item = await ReviewOfAsync(bytes);
        Assert.That((item.Status, item.ArchiveEntry), Is.EqualTo(("Pending", (string?)"dump.nsp")));
        var failing = ActivatorUtilities.CreateInstance<ReviewResolver>(Factory.Services,
            ExternalLibraryTests.FailFirstResolution.Wrap(Service<IIngestReviewRepository>()));
        var first = await failing.ResolveAsync(item.Id, Dat($"Orbit Expedition [{BaseId}]"), await AdminAsync(), default);
        Assert.That(first.Code, Is.EqualTo(IngestErrors.DbWriteFailed), "committed, but the item stayed Pending");
        RefreshWithoutEntries();

        var retry = await Resolver().ResolveAsync(item.Id, Dat($"Orbit Expedition [{BaseId}]"), await AdminAsync(), default);

        Assert.That(retry.Code, Is.Null, "the committed release is recognised before the now-missing entry is looked up");
        Assert.That((await ReviewOfAsync(bytes)).Status, Is.EqualTo("Resolved"));
        Assert.That(ReadGame().Roms.Single().Identification.Method, Is.EqualTo(IdentificationMethod.TitleDb));
    }

    private async Task<long> RomIdAsync(byte[] bytes)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        return await db.QuerySingleAsync<long>("SELECT id FROM roms WHERE sha1 = @sha1", new { sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)) });
    }

    /// <summary>Base, update and DLC in one game.</summary>
    private async Task<Guid> ThreeReleasesAsync()
    {
        var gameId = await IngestBaseAsync();
        Assert.That(Code(await IngestAsync(Drop($"orbit [{UpdateId}][v65536].nsp", UpdateRom))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        Assert.That(Code(await IngestAsync(Drop($"orbit [{DlcId}].nsp", DlcRom))), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        Assert.That(ReadGame().Roms.Single(r => r.IsPrimary).Flags, Is.Empty, "T6: the base is primary");
        return gameId;
    }

    [Test]
    public async Task RemovingTheBase_PromotesExactlyOneRemainingRelease()
    {
        await StartAsync();
        var gameId = await ThreeReleasesAsync();

        var result = await Service<LibraryEditor>().DeleteReleaseAsync(gameId, await RomIdAsync(BaseRom), await AdminAsync(), default);

        Assert.That(result.Code, Is.Null);
        var roms = ReadGame().Roms;
        Assert.That(roms, Has.Count.EqualTo(2));
        Assert.That(roms.Count(r => r.IsPrimary), Is.EqualTo(1));
    }

    [Test]
    public async Task MovingTheBaseToAnotherGame_PromotesOneReleaseInTheSource()
    {
        await StartAsync();
        var gameId = await ThreeReleasesAsync();

        var result = await Service<LibraryEditor>().MoveReleaseAsync(gameId, await RomIdAsync(BaseRom), null, "Orbit Base", await AdminAsync(), default);

        Assert.That(result.Code, Is.Null);
        var source = ReadGame().Roms;
        Assert.That(source.Select(r => r.Flags.Single()), Is.EquivalentTo(new[] { "Update", "DLC" }));
        Assert.That(source.Count(r => r.IsPrimary), Is.EqualTo(1));
        Assert.That(ReadGame(Path.Combine(Bucket, "Orbit Base.zip")).Roms.Single().IsPrimary, Is.True);
    }

    // --- T8 / step 23d: fetched JPEG storage through a titledb caller ---

    private static readonly byte[] BoxJpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 8, 0, 32, 0, 64, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];
    private static readonly byte[] SnapJpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 8, 0, 72, 1, 0, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9];

    [Test]
    public async Task NewGame_StoresTheFetchedJpegs_AsJpgEntriesMatchingTheirMetadata()
    {
        var cdn = new StubHttpHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            Cdn + "box.jpg" => StubHttpHandler.Bytes(BoxJpeg),
            Cdn + "snap.jpg" => StubHttpHandler.Bytes(SnapJpeg),
            _ => StubHttpHandler.Status(System.Net.HttpStatusCode.NotFound),
        });
        await StartAsync(cdn: cdn);

        var gameId = await IngestBaseAsync();

        Assert.That(cdn.Requests.Select(r => r.RequestUri!.AbsoluteUri), Is.EquivalentTo(new[] { Cdn + "box.jpg", Cdn + "snap.jpg" }));
        var game = ReadGame();
        Assert.That(game.Images.Select(i => (i.FileName, i.Type, i.Source, i.SourceUrl, i.IsCover, i.IsDefault, i.Width, i.Height)), Is.EqualTo(new[]
        {
            ("boxart-01.jpg", ImageType.BoxArt, InfoJsonImage.SourceTitleDb, (string?)(Cdn + "box.jpg"), true, false, 64, 32),
            ("snap-01.jpg", ImageType.Snap, InfoJsonImage.SourceTitleDb, (string?)(Cdn + "snap.jpg"), false, false, 256, 72),
        }));
        // The ZIP's actual bytes, against what info.json records for them.
        using (var zip = System.IO.Compression.ZipFile.OpenRead(ZipPath))
        {
            foreach (var (image, bytes) in game.Images.Zip([BoxJpeg, SnapJpeg]))
            {
                using var entry = zip.GetEntry(LibraryZip.ImageEntry(image.FileName))!.Open();
                using var copy = new MemoryStream();
                entry.CopyTo(copy);
                Assert.That(copy.ToArray(), Is.EqualTo(bytes), image.FileName);
                Assert.That((image.SizeBytes, image.Sha1), Is.EqualTo(((long)bytes.Length, Convert.ToHexStringLower(SHA1.HashData(bytes)))));
            }
            Assert.That(zip.Entries.Any(e => e.FullName.EndsWith(".png")), Is.False);
        }
        // And the mirror.
        await using var db = Service<IDbConnectionFactory>().Create();
        var rows = (await db.QueryAsync<(string File, string Source, string? Url)>(
            "SELECT file_name, source, source_url FROM images WHERE game_id = @id ORDER BY file_name", new { id = gameId.ToString() })).ToList();
        Assert.That(rows, Is.EqualTo(new[]
        {
            ("boxart-01.jpg", InfoJsonImage.SourceTitleDb, (string?)(Cdn + "box.jpg")),
            ("snap-01.jpg", InfoJsonImage.SourceTitleDb, (string?)(Cdn + "snap.jpg")),
        }));
    }
}
