using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>
/// Bundle Creator step 6: the single-file routes keep their names, bytes and range rules after moving onto the shared
/// <see cref="LibraryDownload"/> opener, and the opener hands out an owned stream with its expected length.
/// </summary>
public partial class LibraryEndpointsTests
{
    private async Task<(InfoJsonGame Game, long UsaRomId, long Track2Id)> SetGameWithMainFileAsync()
    {
        var game = await SetGame();
        await File.WriteAllBytesAsync(Path.Combine(RomsFolder("extset", "D"), "Disc (USA) (Track 1).bin"), DiscTrack1);
        var usa = Release(await GetJson($"/api/games/{game.GameId}"), "Disc (USA) (Track 1).bin");
        return (game, usa.GetProperty("id").GetInt64(), usa.GetProperty("files")[1].GetProperty("id").GetInt64());
    }

    private static DefaultHttpContext Context() => new();

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>True once nothing else holds the file open (an exclusive open succeeds).</summary>
    private static bool IsClosed(string path)
    {
        try
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [Test]
    public async Task ExternalMainFileDownload_KeepsItsNameAndRanges()
    {
        var (game, romId, _) = await SetGameWithMainFileAsync();
        var url = $"/api/games/{game.GameId}/roms/{romId}/download";

        using var response = await _user.GetAsync(url);
        using var partial = await _user.SendAsync(HttpMethod.Get, url, null, sendXsrf: false, headers => headers.Range = new RangeHeaderValue(5, 9));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentDisposition?.DispositionType, Is.EqualTo("attachment"));
        Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar, Is.EqualTo("Disc (USA) (Track 1).bin"));
        Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(DiscTrack1.Length));
        Assert.That(response.Headers.AcceptRanges, Does.Contain("bytes"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(DiscTrack1));
        Assert.That(partial.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
        Assert.That(await partial.Content.ReadAsByteArrayAsync(), Is.EqualTo(DiscTrack1[5..10]));
    }

    [Test]
    public async Task EmbeddedDownload_StillIgnoresRanges_AndNamesTheEntry()
    {
        var id = await Ingest("p.gb", Pinball);
        var url = (await GetJson($"/api/games/{id}")).GetProperty("roms")[0].GetProperty("downloadUrl").GetString()!;

        using var ranged = await _user.SendAsync(HttpMethod.Get, url, null, sendXsrf: false, headers => headers.Range = new RangeHeaderValue(0, 1));

        Assert.That(ranged.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ranged.Content.Headers.ContentDisposition?.FileNameStar, Is.EqualTo("Pokémon Pinball (Europe).gb"));
        Assert.That(await ranged.Content.ReadAsByteArrayAsync(), Is.EqualTo(Pinball));
    }

    [Test]
    public async Task Opener_Embedded_IsAnOwnedStream_WithTheEntrysLength()
    {
        var id = await Ingest("p.gb", Pinball);
        var romId = (await GetJson($"/api/games/{id}")).GetProperty("roms")[0].GetProperty("id").GetInt64();
        var zip = await ZipPath(id);

        var (attachment, problem) = await LibraryDownload.OpenReleaseAsync(Context(), id, romId,
            Service<IGameRepository>(), Service<IRomRepository>(), Service<LibraryFiles>());

        Assert.That(problem, Is.Null);
        Assert.That((attachment!.Length, attachment.FileName, attachment.Ranges), Is.EqualTo(((long)Pinball.Length, "Pokémon Pinball (Europe).gb", false)));
        Assert.That(IsClosed(zip), Is.False, "the ZIP is open while the stream is");
        Assert.That(await ReadAllAsync(attachment.Stream), Is.EqualTo(Pinball));
        await attachment.DisposeAsync();
        Assert.That(IsClosed(zip), Is.True, "disposing the stream closes its ZIP");
    }

    [Test]
    public async Task Opener_ExternalFiles_AreOwnedStreams_WithTheFilesLength()
    {
        var (game, romId, track2Id) = await SetGameWithMainFileAsync();
        var folder = RomsFolder("extset", "D");

        var (main, mainProblem) = await LibraryDownload.OpenReleaseAsync(Context(), game.GameId, romId,
            Service<IGameRepository>(), Service<IRomRepository>(), Service<LibraryFiles>());
        var (track2, setProblem) = await LibraryDownload.OpenSetFileAsync(Context(), game.GameId, romId, track2Id,
            Service<IGameRepository>(), Service<IRomRepository>(), Service<LibraryFiles>());

        Assert.That((mainProblem, setProblem), Is.EqualTo(((IResult?)null, (IResult?)null)));
        Assert.That((main!.Length, main.FileName, main.Ranges), Is.EqualTo(((long)DiscTrack1.Length, "Disc (USA) (Track 1).bin", true)));
        Assert.That((track2!.Length, track2.FileName), Is.EqualTo(((long)DiscTrack2.Length, "Disc (USA) (Track 2).bin")));
        Assert.That(await ReadAllAsync(track2.Stream), Is.EqualTo(DiscTrack2));
        await main.DisposeAsync();
        await track2.DisposeAsync();
        Assert.That(IsClosed(Path.Combine(folder, "Disc (USA) (Track 1).bin")) && IsClosed(Path.Combine(folder, "Disc (USA) (Track 2).bin")), Is.True);
    }

    [Test]
    public async Task Opener_AMissingFileOrForeignId_IsTheRoutes404()
    {
        var (game, romId, track2Id) = await SetGameWithMainFileAsync();
        File.Delete(Path.Combine(RomsFolder("extset", "D"), "Disc (USA) (Track 2).bin"));

        var (gone, goneProblem) = await LibraryDownload.OpenSetFileAsync(Context(), game.GameId, romId, track2Id,
            Service<IGameRepository>(), Service<IRomRepository>(), Service<LibraryFiles>());
        var (foreign, foreignProblem) = await LibraryDownload.OpenReleaseAsync(Context(), Guid.NewGuid(), romId,
            Service<IGameRepository>(), Service<IRomRepository>(), Service<LibraryFiles>());
        using var route = await _user.GetAsync($"/api/games/{game.GameId}/roms/{romId}/files/{track2Id}/download");

        Assert.That((gone, foreign), Is.EqualTo(((LibraryAttachment?)null, (LibraryAttachment?)null)));
        Assert.That(new[] { goneProblem, foreignProblem }, Has.None.Null);
        Assert.That(route.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
