using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Http;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §11 move release + Merge into… (D23, D32, D35), §23 move/merge rows (S34), against MariaDB + real ZIPs.</summary>
[Category("Integration")]
public class LibraryMoveMergeTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] TetrisWorld = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] TetrisJapan = new SyntheticRomBuilder { Seed = 2, Title = "TETRIS" }.Build();
    private static readonly byte[] TetrisUsa = new SyntheticRomBuilder { Seed = 3, Title = "TETRIS" }.Build();
    private static readonly byte[] Zelda = new SyntheticRomBuilder { Seed = 4, Title = "ZELDA" }.Build();
    private static readonly byte[] Color = new SyntheticRomBuilder { Seed = 5, CgbFlag = 0xC0 }.Build();

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
        // Thumbnails: Named_Boxarts/<name>.png is 300×400, Named_Snaps/<name>.png 160×144, for the names a test lists.
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
            [Gb] =
            [
                new FixtureGame("Tetris (World)", "Tetris (World).gb", TetrisWorld) { ReleaseYear = 1989, Developers = ["Nintendo"] },
                new FixtureGame("Tetris (Japan)", "Tetris (Japan).gb", TetrisJapan) { ReleaseYear = 1990, Genres = ["Puzzle"] },
                new FixtureGame("Tetris (USA)", "Tetris (USA).gb", TetrisUsa),
                new FixtureGame("Zelda (USA)", "Zelda (USA).gb", Zelda) { Genres = ["Action"] },
            ],
            ["Nintendo - Game Boy Color"] = [new FixtureGame("Color Game (World)", "Color Game (World).gbc", Color)],
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

    private async Task<IngestItemResult> Ingest(string name, byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", "gb", name);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var file = new IngestFile("gb", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
        return (await Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default))!;
    }

    private async Task<Guid> Game(params (string Name, byte[] Bytes)[] files)
    {
        Guid? id = null;
        foreach (var (name, bytes) in files)
            id = (await Ingest(name, bytes)).GameId;
        return id!.Value;
    }

    private Task<GameRow?> Row(Guid id) => Service<IGameRepository>().GetAsync(id, default);

    private string Abs(string zipPath) => Path.Combine(_factory.Root, "roms", zipPath);

    private InfoJsonGame ReadZip(string zipPath)
    {
        using var zip = LibraryZip.Open(Abs(zipPath), Service<InfoJsonReader>());
        return zip.InfoJson!.Game!;
    }

    private async Task<InfoJsonGame> ReadGame(Guid id) => ReadZip((await Row(id))!.ZipPath);

    private async Task<long> RomId(Guid gameId, string fileName) =>
        (await Service<IRomRepository>().ListByGameAsync(gameId, default)).Single(r => r.FileName == fileName).Id;

    private Task<HttpResponseMessage> Move(Guid id, long romId, object body) => _admin.PostAsync($"/api/games/{id}/roms/{romId}/move", body);

    private Task<HttpResponseMessage> Merge(Guid id, Guid target) => _admin.PostAsync($"/api/games/{id}/merge", new { targetGameId = target });

    private static async Task<Guid> TargetId(HttpResponseMessage response)
    {
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return (await AuthClient.JsonAsync(response)).GetProperty("targetGameId").GetGuid();
    }

    private static IEnumerable<TestCaseData> Endpoints()
    {
        var id = Guid.NewGuid();
        yield return new TestCaseData($"/api/games/{id}/roms/1/move").SetName("RoleMatrix POST move");
        yield return new TestCaseData($"/api/games/{id}/merge").SetName("RoleMatrix POST merge");
    }

    [TestCaseSource(nameof(Endpoints))]
    public async Task RoleMatrix_AnonymousIs401_UserIs403_XsrfRequired(string url)
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await user.LoginAsync("plain", TestUsers.Password)).Dispose();
        var body = new { targetGameId = Guid.NewGuid() };

        using var anonymousResponse = await anonymous.PostAsync(url, body);
        using var userResponse = await user.PostAsync(url, body);
        using var noXsrf = await _admin.PostAsync(url, body, sendXsrf: false);

        Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(userResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await AuthClient.CodeAsync(noXsrf), Is.EqualTo("AntiforgeryInvalid"));
    }

    [Test]
    public async Task MoveToExistingGame_RebuildsBoth_KeepsBaseTitle_ThenSharedBaseTitleIsAmbiguous()
    {
        var tetris = await Game(("t1.gb", TetrisWorld), ("t2.gb", TetrisJapan));
        var zelda = await Game(("z.gb", Zelda));

        using var response = await Move(tetris, await RomId(tetris, "Tetris (Japan).gb"), new { targetGameId = zelda });

        Assert.That(await TargetId(response), Is.EqualTo(zelda));
        var source = await ReadGame(tetris);
        Assert.That(source.Roms.Select(r => (r.FileName, r.IsPrimary)), Is.EqualTo(new[] { ("Tetris (World).gb", true) }));
        var target = await ReadGame(zelda);
        Assert.That(target.Title, Is.EqualTo("Zelda"));
        Assert.That(target.Roms.Select(r => (r.FileName, r.BaseTitle)), Is.EquivalentTo(new[] { ("Zelda (USA).gb", "Zelda"), ("Tetris (Japan).gb", "Tetris") }));
        Assert.That(target.Roms.Count(r => r.IsPrimary), Is.EqualTo(1));
        Assert.That(target.Genres, Is.EqualTo(new[] { "Action" }), "an existing target's metadata isn't touched");
        var rows = await Service<IRomRepository>().ListByGameAsync(zelda, default);
        Assert.That(rows.Select(r => r.FileName), Is.EquivalentTo(new[] { "Zelda (USA).gb", "Tetris (Japan).gb" }));

        // D32: both games now own base title "Tetris".
        var next = await Ingest("t3.gb", TetrisUsa);
        Assert.That((next.Outcome, next.Reason), Is.EqualTo((IngestOutcome.Review, "AmbiguousGroup")));
    }

    [Test]
    public async Task MovingTheOnlyRelease_DeletesTheSource_AndLaterReleasesFollowIt()
    {
        var tetris = await Game(("t1.gb", TetrisWorld));
        var zelda = await Game(("z.gb", Zelda));
        var sourceZip = (await Row(tetris))!.ZipPath;

        using var response = await Move(tetris, await RomId(tetris, "Tetris (World).gb"), new { targetGameId = zelda });

        Assert.That(await TargetId(response), Is.EqualTo(zelda));
        Assert.That(await Row(tetris), Is.Null);
        Assert.That(File.Exists(Abs(sourceZip)), Is.False);
        Assert.That((await ReadGame(zelda)).Roms, Has.Count.EqualTo(2));

        // D23: the moved ROM keeps its base title, so the next Tetris release joins Zelda.
        var next = await Ingest("t2.gb", TetrisJapan);
        Assert.That((next.Outcome, next.GameId), Is.EqualTo((IngestOutcome.Ingested, (Guid?)zelda)));
    }

    [Test]
    public async Task MoveToNewGame_RunsMetadataAndMediaForIt()
    {
        _boxArt.Add("Tetris (Japan)");
        var tetris = await Game(("t1.gb", TetrisWorld), ("t2.gb", TetrisJapan));

        using var response = await Move(tetris, await RomId(tetris, "Tetris (Japan).gb"), new { newGameTitle = "  Tetris, The " });

        var id = await TargetId(response);
        var row = (await Row(id))!;
        Assert.That((row.ZipPath, row.Title, row.DisplayTitle), Is.EqualTo(("gb/T/Tetris, The.zip", "Tetris, The", "The Tetris")));
        var game = ReadZip(row.ZipPath);
        Assert.That(game.Roms.Select(r => (r.FileName, r.BaseTitle, r.IsPrimary)), Is.EqualTo(new[] { ("Tetris (Japan).gb", "Tetris", true) }));
        Assert.That((game.ReleaseYear, game.Genres.Single(), game.Developers.Count), Is.EqualTo(((int?)1990, "Puzzle", 0)), "metadata of the moved release's DAT entry");
        var boxArt = game.Images.Single(i => i.Type == ImageType.BoxArt);
        Assert.That((boxArt.IsDefault, boxArt.Width), Is.EqualTo((false, 300)));
        Assert.That((await ReadGame(tetris)).Roms.Select(r => r.FileName), Is.EqualTo(new[] { "Tetris (World).gb" }));
    }

    [Test]
    public async Task Move_Validation_And404s()
    {
        var tetris = await Game(("t1.gb", TetrisWorld), ("t2.gb", TetrisJapan));
        var zelda = await Game(("z.gb", Zelda));
        var color = (await Ingest("c.gb", Color)).GameId!.Value;
        var romId = await RomId(tetris, "Tetris (Japan).gb");

        foreach (var (body, field) in new (object Body, string Field)[]
        {
            (new { }, "targetGameId"),
            (new { targetGameId = zelda, newGameTitle = "New" }, "targetGameId"),
            (new { newGameTitle = " " }, "newGameTitle"),
            (new { targetGameId = tetris }, "targetGameId"),
            (new { targetGameId = color }, "targetGameId"),
        })
        {
            using var response = await Move(tetris, romId, body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), field);
            Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), Is.True, field);
        }

        using (var unknownTarget = await Move(tetris, romId, new { targetGameId = Guid.NewGuid() }))
            Assert.That(unknownTarget.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var staleRom = await Move(zelda, romId, new { targetGameId = tetris }))
            Assert.That(staleRom.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await ReadGame(tetris)).Roms, Has.Count.EqualTo(2), "nothing moved");
    }

    [Test]
    public async Task Merge_MovesEveryRelease_FillsEmptyFields_TakesRealArtOverDefault_DeletesSource()
    {
        _boxArt.Add("Tetris (World)");
        _snaps.Add("Tetris (World)");
        var tetris = await Game(("t1.gb", TetrisWorld), ("t2.gb", TetrisJapan));
        var zelda = await Game(("z.gb", Zelda));
        var sourceZip = (await Row(tetris))!.ZipPath;
        Assert.That((await ReadGame(zelda)).Images.Single().IsDefault, Is.True);

        using var response = await Merge(tetris, zelda);

        Assert.That(await TargetId(response), Is.EqualTo(zelda));
        Assert.That(await Row(tetris), Is.Null);
        Assert.That(File.Exists(Abs(sourceZip)), Is.False);
        var game = await ReadGame(zelda);
        Assert.That((game.Title, game.ReleaseYear), Is.EqualTo(("Zelda", (int?)1989)));
        Assert.That(game.Developers, Is.EqualTo(new[] { "Nintendo" }), "empty field filled");
        Assert.That(game.Genres, Is.EqualTo(new[] { "Action" }), "target value kept");
        Assert.That(game.Roms.Select(r => r.FileName), Is.EquivalentTo(new[] { "Zelda (USA).gb", "Tetris (World).gb", "Tetris (Japan).gb" }));
        Assert.That(game.Roms.Count(r => r.IsPrimary), Is.EqualTo(1));
        // I4a: the target's placeholder (its cover) gives way to the source's real box art, copied under a new name.
        var boxArt = game.Images.Single(i => i.Type == ImageType.BoxArt);
        Assert.That((boxArt.IsDefault, boxArt.Width, boxArt.FileName, boxArt.IsCover), Is.EqualTo((false, 300, "boxart-02.png", true)));
        Assert.That(game.Images.Single(i => i.Type == ImageType.Snap).Width, Is.EqualTo(160));
        var row = (await Row(zelda))!;
        Assert.That((row.HasDefaultBoxArt, row.ReleaseYear), Is.EqualTo((false, (int?)1989)));
        Assert.That(await Service<IRomRepository>().ListByGameAsync(zelda, default), Has.Count.EqualTo(3));

        // Releases keep their base title, so later Tetris releases join the target.
        var next = await Ingest("t3.gb", TetrisUsa);
        Assert.That((next.Outcome, next.GameId), Is.EqualTo((IngestOutcome.Ingested, (Guid?)zelda)));
    }

    private async Task Rate(Guid id, int? rating, bool favourite) =>
        Assert.That((await Service<LibraryEditor>().PatchAsync(id, new GamePatch { SetRating = true, Rating = rating, Favourite = favourite }, null, default)).Code, Is.Null);

    [TestCase(null, false, 3, true, 3, true, TestName = "Merge rating: unrated target takes the source's; favourite OR")]
    [TestCase(5, false, 3, false, 5, false, TestName = "Merge rating: target keeps its own")]
    [TestCase(2, true, null, false, 2, true, TestName = "Merge rating: target favourite kept when source isn't")]
    public async Task Merge_RatingAndFavourite(int? targetRating, bool targetFav, int? sourceRating, bool sourceFav, int? rating, bool favourite)
    {
        var tetris = await Game(("t1.gb", TetrisWorld));
        var zelda = await Game(("z.gb", Zelda));
        await Rate(zelda, targetRating, targetFav);
        await Rate(tetris, sourceRating, sourceFav);

        using var response = await Merge(tetris, zelda);

        Assert.That(await TargetId(response), Is.EqualTo(zelda));
        var game = await ReadGame(zelda);
        var row = (await Row(zelda))!;
        Assert.That((game.Rating, game.Favourite, row.Rating, row.Favourite), Is.EqualTo((rating, favourite, rating, favourite)));
    }

    [Test]
    public async Task MoveToNewGame_StartsUnratedNotFavourite_SourceKeepsItsValues()
    {
        var tetris = await Game(("t1.gb", TetrisWorld), ("t2.gb", TetrisJapan));
        await Rate(tetris, 4, true);

        using var response = await Move(tetris, await RomId(tetris, "Tetris (Japan).gb"), new { newGameTitle = "Tetris JP" });

        var moved = await ReadGame(await TargetId(response));
        var source = await ReadGame(tetris);
        Assert.That((moved.Rating, moved.Favourite, source.Rating, source.Favourite), Is.EqualTo(((int?)null, false, (int?)4, true)));
        Assert.That(moved.Images.Single(i => i.IsCover).Type, Is.EqualTo(ImageType.BoxArt), "the new game's box art (or placeholder) is its cover");
    }

    [Test]
    public async Task Merge_TargetKeepsItsOwnImagesAndCover_AndGainsTheSourceImagesRenamed()
    {
        _boxArt.UnionWith(["Tetris (World)", "Zelda (USA)"]);
        _snaps.UnionWith(["Tetris (World)", "Zelda (USA)"]);
        var tetris = await Game(("t1.gb", TetrisWorld));
        var zelda = await Game(("z.gb", Zelda));
        var before = (await ReadGame(zelda)).Images;

        using var response = await Merge(tetris, zelda);

        Assert.That(await TargetId(response), Is.EqualTo(zelda));
        var after = (await ReadGame(zelda)).Images;
        Assert.That(after.Take(before.Count), Is.EqualTo(before), "the target's images and cover are unchanged");
        Assert.That(after.Skip(before.Count).Select(i => (i.FileName, i.IsCover, i.SourceUrl!.Contains("Tetris", StringComparison.Ordinal))),
            Is.EqualTo(new[] { ("boxart-02.png", false, true), ("snap-02.png", false, true) }));
    }

    [Test]
    public async Task Merge_OverTwentyImages_Is409TooManyImages_AndChangesNothing()
    {
        var tetris = await Game(("t1.gb", TetrisWorld));
        var zelda = await Game(("z.gb", Zelda));
        // Hand-build a source with 20 uploaded snaps; with the target's placeholder that is 21 (uploads come in a later step).
        var source = (await Row(tetris))!;
        var game = await ReadGame(tetris);
        var png = (await ReadGame(zelda)).Images[0];
        var snaps = Enumerable.Range(1, 20).Select(n => png with
        {
            FileName = $"snap-{n:00}.png", Type = ImageType.Snap, Source = InfoJsonImage.SourceUpload, IsDefault = false, IsCover = false,
        }).ToList();
        var placeholderBytes = Service<ISystemRegistry>().ById("gb")!.DefaultBoxArtPng.ToArray();
        var reader = Service<InfoJsonReader>();
        await new LibraryZipWriter(reader).WriteAsync(Abs(source.ZipPath), LibraryZip.Open(Abs(source.ZipPath), reader), new LibraryZipChanges(
            game with { Images = [snaps[0] with { IsCover = true }, .. snaps.Skip(1)] },
            snaps.ToDictionary(i => LibraryZip.ImageEntry(i.FileName), _ => (Func<Stream>)(() => new MemoryStream(placeholderBytes)))));
        var targetBefore = await ReadGame(zelda);

        using var response = await Merge(tetris, zelda);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("TooManyImages"));
        Assert.That(await Row(tetris), Is.Not.Null);
        Assert.That((await ReadGame(zelda)).Images, Is.EqualTo(targetBefore.Images));
    }

    [Test]
    public async Task Merge_Validation_And404()
    {
        var tetris = await Game(("t1.gb", TetrisWorld));
        var color = (await Ingest("c.gb", Color)).GameId!.Value;

        using (var self = await Merge(tetris, tetris))
            Assert.That(self.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using (var otherSystem = await Merge(tetris, color))
        {
            Assert.That(otherSystem.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await AuthClient.CodeAsync(otherSystem), Is.EqualTo("ValidationFailed"));
        }
        using (var missing = await _admin.PostAsync($"/api/games/{tetris}/merge", new { }))
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using (var unknown = await Merge(tetris, Guid.NewGuid()))
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await Row(tetris), Is.Not.Null);
    }

    [Test]
    public async Task Move_SourceRebuildFails_TargetIsPutBack()
    {
        var tetris = await Game(("t1.gb", TetrisWorld), ("t2.gb", TetrisJapan));
        var zelda = await Game(("z.gb", Zelda));
        var sourcePath = Abs((await Row(tetris))!.ZipPath);
        // The Japan entry (which stays behind) is corrupt, so rebuilding the source fails after the target was written.
        using (var zip = ZipFile.Open(sourcePath, ZipArchiveMode.Update))
        {
            zip.GetEntry("roms/Tetris (Japan).gb")!.Delete();
            using var stream = zip.CreateEntry("roms/Tetris (Japan).gb", CompressionLevel.NoCompression).Open();
            stream.Write(new byte[TetrisJapan.Length + 10]);
        }

        using var response = await Move(tetris, await RomId(tetris, "Tetris (World).gb"), new { targetGameId = zelda });

        Assert.That((response.StatusCode, await AuthClient.CodeAsync(response)), Is.EqualTo((HttpStatusCode.InternalServerError, "VerifyFailed")));
        Assert.That((await ReadGame(zelda)).Roms.Select(r => r.FileName), Is.EqualTo(new[] { "Zelda (USA).gb" }), "target put back");
        Assert.That((await ReadGame(tetris)).Roms, Has.Count.EqualTo(2), "source unchanged");
        Assert.That(await Service<IRomRepository>().ListByGameAsync(zelda, default), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task MoveAndMerge_WhileAJobRuns_Are409()
    {
        var tetris = await Game(("t1.gb", TetrisWorld), ("t2.gb", TetrisJapan));
        var zelda = await Game(("z.gb", Zelda));
        var romId = await RomId(tetris, "Tetris (Japan).gb");
        using var held = Service<JobLock>().TryAcquire();

        using var move = await Move(tetris, romId, new { targetGameId = zelda });
        using var merge = await Merge(tetris, zelda);

        Assert.That((move.StatusCode, await AuthClient.CodeAsync(move)), Is.EqualTo((HttpStatusCode.Conflict, "JobRunning")));
        Assert.That((merge.StatusCode, await AuthClient.CodeAsync(merge)), Is.EqualTo((HttpStatusCode.Conflict, "JobRunning")));
    }
}
