using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Release notes B3–B7: <c>/api/games/{id}/roms/{romId}/note</c> and <c>hasNote</c>, against MariaDB + real ZIPs.</summary>
[Category("Integration")]
public class NoteEndpointsTests
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
        await TestUsers.CreateAsync(_factory, "plain"); // no roles: notes are open to every signed-in user
        (await _user.LoginAsync("plain", TestUsers.Password)).Dispose();
        var cache = Service<DatCache>();
        FixtureDatWriter.Write(cache.Directory, new Dictionary<string, IReadOnlyList<FixtureGame>>
        {
            [IngestSeeder.GbaDat] = [],
            [IngestSeeder.N64Dat] = [],
            [IngestSeeder.NesDat] = [],
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

    /// <summary>ROM ids are re-created by every ZIP write's mirror, so they're looked up per call.</summary>
    private async Task<long> World() =>
        (await Service<IRomRepository>().ListByGameAsync(_game, default)).Single(r => r.FileName == "Tetris (World).gb").Id;

    private async Task<string> Url(long? romId = null) => $"/api/games/{_game}/roms/{romId ?? await World()}/note";

    private async Task<HttpResponseMessage> Put(object body, long? romId = null) => await _user.SendAsync(HttpMethod.Put, await Url(romId), body, true);

    private async Task<HttpResponseMessage> Delete() => await _user.SendAsync(HttpMethod.Delete, await Url(), null, true);

    private async Task<string> ZipPath() => Path.Combine(_factory.Root, "roms", (await Service<IGameRepository>().GetAsync(_game, default))!.ZipPath);

    private static async Task Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
            Assert.That(response.StatusCode, Is.EqualTo(status), await response.Content.ReadAsStringAsync());
    }

    private async Task<Dictionary<string, bool>> HasNote()
    {
        using var response = await _user.GetAsync($"/api/games/{_game}");
        var body = await AuthClient.JsonAsync(response);
        return body.GetProperty("roms").EnumerateArray().ToDictionary(r => r.GetProperty("fileName").GetString()!, r => r.GetProperty("hasNote").GetBoolean());
    }

    [Test]
    public async Task PutThenGet_RoundTrips_NormalisesLineEndings_AndSetsHasNote()
    {
        Assert.That(await HasNote(), Is.EquivalentTo(new Dictionary<string, bool> { ["Tetris (World).gb"] = false, ["Tetris (Japan).gb"] = false }));

        await Expect(await Put(new { text = "# Hi\r\n\r\n- ü\r" }), HttpStatusCode.NoContent);

        using var get = await _user.GetAsync(await Url());
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(get.Content.Headers.ContentType!.ToString(), Is.EqualTo("text/markdown; charset=utf-8"));
        Assert.That(await get.Content.ReadAsByteArrayAsync(), Is.EqualTo(Encoding.UTF8.GetBytes("# Hi\n\n- ü\n")), "UTF-8, no BOM");
        Assert.That(await HasNote(), Is.EquivalentTo(new Dictionary<string, bool> { ["Tetris (World).gb"] = true, ["Tetris (Japan).gb"] = false }));
    }

    [Test]
    public async Task NoteWrite_LeavesInfoJsonUnchanged_AndKeepsTheZipSizeAndMtimeInSync()
    {
        var path = await ZipPath();
        byte[] InfoJson()
        {
            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry(LibraryZip.InfoJsonEntry)!.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        var before = InfoJson();

        await Expect(await Put(new { text = "note" }), HttpStatusCode.NoContent);

        Assert.That(InfoJson(), Is.EqualTo(before), "not a game edit: updatedAt untouched");
        var row = (await Service<IGameRepository>().GetAsync(_game, default))!;
        var file = new FileInfo(path);
        // What the quick check / DbSync compare (§12.1): an unchanged ZIP is skipped.
        Assert.That((row.ZipSizeBytes, row.ZipMtimeUtc), Is.EqualTo((file.Length, MirrorEntry.WholeSeconds(file.LastWriteTimeUtc))));
    }

    [Test]
    public async Task WhitespacePut_DeletesTheNote_AndIs204EvenWithoutOne()
    {
        await Expect(await Put(new { text = " \r\n\t" }), HttpStatusCode.NoContent);
        await Expect(await Put(new { text = "x" }), HttpStatusCode.NoContent);

        await Expect(await Put(new { text = "   " }), HttpStatusCode.NoContent);

        await Expect(await _user.GetAsync(await Url()), HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Delete_RemovesTheNote_ThenIs404()
    {
        await Expect(await Put(new { text = "x" }), HttpStatusCode.NoContent);

        await Expect(await Delete(), HttpStatusCode.NoContent);

        await Expect(await Delete(), HttpStatusCode.NotFound);
        Assert.That((await HasNote())["Tetris (World).gb"], Is.False);
    }

    [TestCase(65536, HttpStatusCode.NoContent)]
    [TestCase(65537, HttpStatusCode.BadRequest)]
    public async Task Put_SizeLimitIsCountedInUtf8BytesAfterNormalisation(int bytes, HttpStatusCode status)
    {
        // "é" is 2 UTF-8 bytes; "\r\n" counts as 1 after normalisation.
        var text = new string('é', (bytes - 1) / 2) + ((bytes - 1) % 2 == 1 ? "a" : "") + "\r\n";
        using var response = await Put(new { text });

        Assert.That(response.StatusCode, Is.EqualTo(status), await response.Content.ReadAsStringAsync());
        if (status == HttpStatusCode.BadRequest)
            Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty("text", out _), Is.True);
    }

    [Test]
    public async Task Put_MissingOrNonStringText_Is400()
    {
        foreach (var body in new object[] { new { }, new { text = 3 }, new { text = (string?)null } })
        {
            using var response = await Put(body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await AuthClient.JsonAsync(response)).GetProperty("errors").TryGetProperty("text", out _), Is.True);
        }
    }

    [Test]
    public async Task UnknownGameOrRelease_Is404()
    {
        await Expect(await _user.GetAsync(await Url(999_999)), HttpStatusCode.NotFound);
        await Expect(await Put(new { text = "x" }, 999_999), HttpStatusCode.NotFound);
        await Expect(await _user.SendAsync(HttpMethod.Put, $"/api/games/{Guid.NewGuid()}/roms/{await World()}/note", new { text = "x" }, true), HttpStatusCode.NotFound);
    }

    [Test]
    public async Task WhileAJobRuns_PutAndDeleteAre409JobRunning()
    {
        using var held = Service<JobLock>().TryAcquire();

        using var put = await Put(new { text = "x" });
        using var delete = await Delete();

        Assert.That((put.StatusCode, await AuthClient.CodeAsync(put)), Is.EqualTo((HttpStatusCode.Conflict, "JobRunning")));
        Assert.That((delete.StatusCode, await AuthClient.CodeAsync(delete)), Is.EqualTo((HttpStatusCode.Conflict, "JobRunning")));
    }

    [Test]
    public async Task Get_OversizedNote_Is409NoteTooLarge()
    {
        using (var zip = ZipFile.Open(await ZipPath(), ZipArchiveMode.Update))
        using (var stream = zip.CreateEntry(LibraryZip.NoteEntry("Tetris (World).gb")).Open())
            stream.Write(new byte[LibraryZip.MaxNoteBytes + 1]);

        using var response = await _user.GetAsync(await Url());

        Assert.That((response.StatusCode, await AuthClient.CodeAsync(response)), Is.EqualTo((HttpStatusCode.Conflict, "NoteTooLarge")));
    }

    [Test]
    public async Task UnreadableZip_HasNoteFalse_StillReturnsTheGame()
    {
        await File.WriteAllBytesAsync(await ZipPath(), [1, 2, 3]);

        Assert.That((await HasNote()).Values, Is.All.False);
    }
}
