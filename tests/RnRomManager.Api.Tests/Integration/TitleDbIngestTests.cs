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

    private ApiFactory? _factory;

    private ApiFactory Factory => _factory ?? throw new InvalidOperationException("Start() first");

    /// <summary>A host with the extdb handler, its header carrying <paramref name="headerTitleId"/> when set.</summary>
    private async Task StartAsync(string? headerTitleId = null)
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services =>
            {
                services.AddSingleton<ISystemHandler>(new ExternalTitleDbTestHandler(headerTitleId));
                services.AddSingleton<ISystemRegistry, SystemRegistry>();
            },
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = ""; // default art, no network
        FixtureDatWriter.Write(Path.Combine(_factory.Root, "cache", DatCache.FolderName), new Dictionary<string, IReadOnlyList<FixtureGame>>(),
            titleDb: new Dictionary<string, IReadOnlyList<TitleDbRow>>
            {
                [ExternalTitleDbTestHandler.DatNameValue] =
                [
                    new(BaseId, "Orbit Expedition", "Orbit Co", null, 2020, ["Action"], ["en"], null, null),
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
    private async Task<(ReviewItem Item, IngestFile File, Guid First)> AmbiguousAsync()
    {
        var first = await IngestBaseAsync();
        var other = Bytes(9);
        Assert.That(Code(await IngestAsync(Drop("other.nsp", other))), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
        var manual = new ReviewResolutionRequest { Action = "Manual", Title = "Another Game", Regions = [], Languages = [], Flags = [] };
        var second = (await Resolver().ResolveAsync((await ReviewOfAsync(other)).Id, manual, await AdminAsync(), default)).GameId!.Value;
        await using (var db = Service<IDbConnectionFactory>().Create())
            await db.ExecuteAsync("UPDATE roms SET base_title = 'Orbit Expedition' WHERE game_id = @id", new { id = second.ToString() });

        var file = Drop($"orbit [{UpdateId}][v3].nsp", UpdateRom);
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

    [Test]
    public async Task AttachOfAQueuedTitleDbIdentity_IsRefusedUntilStep24()
    {
        await StartAsync();
        var (item, file, first) = await AmbiguousAsync();

        var result = await Resolver().ResolveAsync(item.Id, new ReviewResolutionRequest { Action = "Attach", TargetGameId = first, Flag = "None" },
            await AdminAsync(), default);

        Assert.That((result.Code, result.Errors?.Keys.Single()), Is.EqualTo(("ValidationFailed", (string?)"action")));
        Assert.That((await ReviewOfAsync(UpdateRom)).Status, Is.EqualTo("Pending"));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(UpdateRom));
    }

    [Test]
    public async Task DatPickOfATitleDbEntry_IsRefusedUntilStep24()
    {
        await StartAsync();
        var bytes = Bytes(9);
        var file = Drop("mystery.nsp", bytes);
        Assert.That(Code(await IngestAsync(file)), Is.EqualTo((IngestOutcome.Review, (string?)"NoDatMatch")));
        var request = new ReviewResolutionRequest
        {
            Action = "Dat", DatSystem = ExternalTitleDbTestHandler.SystemId, DatGameName = $"Orbit Expedition [{BaseId}]",
        };

        var result = await Resolver().ResolveAsync((await ReviewOfAsync(bytes)).Id, request, await AdminAsync(), default);

        Assert.That((result.Code, result.Errors?.Keys.Single()), Is.EqualTo(("ValidationFailed", (string?)"datGameName")));
        Assert.That((await ReviewOfAsync(bytes)).Status, Is.EqualTo("Pending"));
        Assert.That(File.ReadAllBytes(file.FullPath), Is.EqualTo(bytes));
        Assert.That(File.Exists(ZipPath), Is.False);
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
}
