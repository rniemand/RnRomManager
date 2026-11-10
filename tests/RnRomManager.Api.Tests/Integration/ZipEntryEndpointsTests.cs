using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Zip Contents Z2, Z3: <c>/api/games/{id}/zip-entries</c> and its entry download, against MariaDB + real ZIPs.</summary>
[Category("Integration")]
public class ZipEntryEndpointsTests
{
    private const string Gb = "Nintendo - Game Boy";
    private static readonly byte[] TetrisWorld = new SyntheticRomBuilder { Seed = 1, Title = "TETRIS" }.Build();
    private static readonly byte[] TetrisJapan = new SyntheticRomBuilder { Seed = 2, Title = "TETRIS" }.Build();

    private ApiFactory _factory = null!;
    private AuthClient _user = null!;
    private Guid _game;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        _user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain"); // no roles: any signed-in user
        (await _user.LoginAsync("plain", TestUsers.Password)).Dispose();
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
            [Gb] =
            [
                new FixtureGame("Tetris (World)", "Tetris (World).gb", TetrisWorld),
                new FixtureGame("Tetris (Japan)", "Tetris (Japan).gb", TetrisJapan),
            ],
        });
        cache.Load();
        _game = await Ingest("t1.gb", TetrisWorld);
        await Ingest("t2.gb", TetrisJapan);
    }

    [TearDown]
    public void TearDown()
    {
        _user.Dispose();
        _factory.Dispose();
    }

    private async Task<Guid> Ingest(string name, byte[] bytes)
    {
        var path = Path.Combine(_factory.Root, "ingest", "gb", name);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var file = new IngestFile("gb", path, name, bytes.Length, File.GetLastWriteTimeUtc(path));
        return (await Service<IngestPipeline>().ProcessAsync(file, Guid.NewGuid(), new CircuitBreaker(), default))!.GameId!.Value;
    }

    private async Task<string> ZipPath() => Path.Combine(_factory.Root, "roms", (await Service<IGameRepository>().GetAsync(_game, default))!.ZipPath);

    private string Download(string path) => $"/api/games/{_game}/zip-entries/download?path={Uri.EscapeDataString(path)}";

    private async Task Expect(string url, HttpStatusCode status, string? code = null)
    {
        using var response = await _user.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(status), url);
        if (code is not null)
            Assert.That((await AuthClient.JsonAsync(response)).GetProperty("code").GetString(), Is.EqualTo(code));
    }

    [Test]
    public async Task List_HasTheZipNameAndEveryFileEntry_WithArchiveSizes_InOrdinalOrder()
    {
        var zipPath = await ZipPath();
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            zip.CreateEntry("extras/"); // a folder entry: left out

        using var response = await _user.GetAsync($"/api/games/{_game}/zip-entries");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await AuthClient.JsonAsync(response);

        Assert.That(body.GetProperty("zipName").GetString(), Is.EqualTo("Tetris.zip"));
        var entries = body.GetProperty("entries").EnumerateArray().ToList();
        var paths = entries.Select(e => e.GetProperty("path").GetString()!).ToList();
        using var archive = ZipFile.OpenRead(zipPath);
        var expected = archive.Entries.Where(e => !e.FullName.EndsWith('/')).OrderBy(e => e.FullName, StringComparer.Ordinal).ToList();
        Assert.That(paths, Is.EqualTo(expected.Select(e => e.FullName)));
        Assert.That(paths, Does.Contain("info.json").And.Contain("roms/Tetris (World).gb").And.Contain("roms/Tetris (Japan).gb"));
        Assert.That(paths.Any(p => p.StartsWith("images/", StringComparison.Ordinal)), Is.True);
        Assert.That(entries.Select(e => (e.GetProperty("sizeBytes").GetInt64(), e.GetProperty("compressedSizeBytes").GetInt64())),
            Is.EqualTo(expected.Select(e => (e.Length, e.CompressedLength))));
        Assert.That(entries[0].GetProperty("modifiedUtc").GetDateTime(), Is.EqualTo(expected[0].LastWriteTime.UtcDateTime));
    }

    [Test]
    public async Task UnknownGameOrMissingZip_Is404_CorruptZip_Is500IoError()
    {
        await Expect($"/api/games/{Guid.NewGuid()}/zip-entries", HttpStatusCode.NotFound);
        var zipPath = await ZipPath();
        await File.WriteAllBytesAsync(zipPath, new byte[100]);
        await Expect($"/api/games/{_game}/zip-entries", HttpStatusCode.InternalServerError, "IoError");
        await Expect(Download("info.json"), HttpStatusCode.InternalServerError, "IoError");
        File.Delete(zipPath);
        await Expect($"/api/games/{_game}/zip-entries", HttpStatusCode.NotFound);
        await Expect(Download("info.json"), HttpStatusCode.NotFound);
    }

    [TestCase("info.json")]
    [TestCase("roms/Tetris (World).gb")]
    public async Task Download_StreamsTheEntryBytes_NamedByItsLastSegment(string path)
    {
        using var response = await _user.GetAsync(Download(path));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(response.Content.Headers.ContentDisposition!.FileNameStar, Is.EqualTo(path[(path.LastIndexOf('/') + 1)..]));
        using var archive = ZipFile.OpenRead(await ZipPath());
        using var expected = new MemoryStream();
        using (var entry = archive.GetEntry(path)!.Open())
            entry.CopyTo(expected);
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(expected.ToArray()));
    }

    [Test]
    public async Task Download_NonAsciiName_UsesFilenameStar()
    {
        using (var zip = ZipFile.Open(await ZipPath(), ZipArchiveMode.Update))
        using (var stream = zip.CreateEntry("extras/Pokémon notes.txt").Open())
            stream.Write("hi"u8);

        using var response = await _user.GetAsync(Download("extras/Pokémon notes.txt"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentDisposition!.FileNameStar, Is.EqualTo("Pokémon notes.txt"));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("hi"));
    }

    [Test]
    public async Task Download_MissingPath_Is400Required()
    {
        foreach (var url in new[] { $"/api/games/{_game}/zip-entries/download", $"/api/games/{_game}/zip-entries/download?path=" })
        {
            using var response = await _user.GetAsync(url);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), url);
            var body = await AuthClient.JsonAsync(response);
            Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("ValidationFailed"));
            Assert.That(body.GetProperty("errors").GetProperty("path")[0].GetString(), Is.EqualTo("Required."));
        }
    }

    [TestCase("roms/")]
    [TestCase("roms")]
    [TestCase("nope")]
    [TestCase("../x")]
    [TestCase("../../appsettings.json")]
    public async Task Download_FolderOrMissingOrTraversalName_Is404(string path) =>
        await Expect(Download(path), HttpStatusCode.NotFound);

    [Test]
    public async Task Download_UnknownGame_Is404() =>
        await Expect($"/api/games/{Guid.NewGuid()}/zip-entries/download?path=info.json", HttpStatusCode.NotFound);

    [Test]
    public async Task Anonymous_Is401()
    {
        using var anonymous = await AuthClient.CreateAsync(_factory);
        foreach (var url in new[] { $"/api/games/{_game}/zip-entries", Download("info.json") })
        {
            using var response = await anonymous.GetAsync(url);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), url);
        }
    }
}
