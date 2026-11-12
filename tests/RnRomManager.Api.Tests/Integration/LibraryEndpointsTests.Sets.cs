using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Library;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Disc systems B20/B21: a release's set files in the detail, the list size and their download route.</summary>
public partial class LibraryEndpointsTests
{
    private static readonly byte[] DiscTrack1 = [.. Enumerable.Range(0, 300).Select(i => (byte)i)];
    private static readonly byte[] DiscCue = "FILE \"Disc (USA) (Track 1).bin\" BINARY\n"u8.ToArray();
    private static readonly byte[] DiscTrack2 = [.. Enumerable.Range(0, 200).Select(i => (byte)(255 - i))];
    private static readonly byte[] OtherCue = "FILE \"Disc (Europe) (Track 1).bin\" BINARY\n"u8.ToArray();

    private static InfoJsonRomFile SetFile(string name, byte[] bytes)
    {
        var m = TestLibrary.Rom(name, bytes).Metadata;
        return new InfoJsonRomFile(name, name, name, m.SizeBytes, m.Crc32, m.Md5, m.Sha1);
    }

    /// <summary>Mirrors "Disc" at <c>extset/D/Disc.zip</c>: a USA set (primary; cue + track 2) and a Europe set (cue only), files on disk.</summary>
    private async Task<InfoJsonGame> SetGame()
    {
        var usa = TestLibrary.Rom("Disc (USA) (Track 1).bin", DiscTrack1) with
        {
            ExternalFileName = "Disc (USA) (Track 1).bin",
            Files = [SetFile("Disc (USA).cue", DiscCue), SetFile("Disc (USA) (Track 2).bin", DiscTrack2)],
        };
        var europe = TestLibrary.Rom("Disc (Europe) (Track 1).bin", [9, 9, 9], isPrimary: false) with
        {
            ExternalFileName = "Disc (Europe) (Track 1).bin",
            Files = [SetFile("Disc (Europe).cue", OtherCue)],
        };
        var game = TestLibrary.Game("Disc", usa, europe) with { System = ExternalSetTestHandler.SystemId };
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, "extset/D/Disc.zip", 100, DateTime.UtcNow), default);
        var folder = RomsFolder("extset", "D");
        await File.WriteAllBytesAsync(Path.Combine(folder, "Disc (USA).cue"), DiscCue);
        await File.WriteAllBytesAsync(Path.Combine(folder, "Disc (USA) (Track 2).bin"), DiscTrack2);
        await File.WriteAllBytesAsync(Path.Combine(folder, "Disc (Europe).cue"), OtherCue);
        return game;
    }

    private static JsonElement Release(JsonElement detail, string fileName) =>
        detail.GetProperty("roms").EnumerateArray().Single(r => r.GetProperty("fileName").GetString() == fileName);

    [Test]
    public async Task SetDetail_ListsFiles_AndTotalSize_AndTheListSizeIncludesThem()
    {
        var game = await SetGame();
        await Ingest("tetris.gb", TetrisWorld); // an embedded game: files null, total = its size

        var detail = await GetJson($"/api/games/{game.GameId}");
        var usa = Release(detail, "Disc (USA) (Track 1).bin");
        var files = usa.GetProperty("files").EnumerateArray().ToList();

        Assert.That(files.Select(f => (f.GetProperty("fileName").GetString(), f.GetProperty("sizeBytes").GetInt64(), f.GetProperty("sha1").GetString())),
            Is.EqualTo(new[] { ("Disc (USA).cue", (long)DiscCue.Length, TestLibrary.Rom("x", DiscCue).Metadata.Sha1),
                ("Disc (USA) (Track 2).bin", (long)DiscTrack2.Length, TestLibrary.Rom("x", DiscTrack2).Metadata.Sha1) }));
        Assert.That(files[0].GetProperty("downloadUrl").GetString(),
            Does.Match($"^/api/games/{game.GameId}/roms/{usa.GetProperty("id").GetInt64()}/files/[0-9]+/download$"));
        Assert.That(usa.GetProperty("totalSizeBytes").GetInt64(), Is.EqualTo(DiscTrack1.Length + DiscCue.Length + DiscTrack2.Length));
        Assert.That(Release(detail, "Disc (Europe) (Track 1).bin").GetProperty("totalSizeBytes").GetInt64(), Is.EqualTo(3 + OtherCue.Length));

        var items = (await GetJson("/api/games")).GetProperty("items").EnumerateArray().ToList();
        var tetris = items.Single(i => i.GetProperty("displayTitle").GetString() == "Tetris");
        Assert.That(items.Single(i => i.GetProperty("id").GetGuid() == game.GameId).GetProperty("primarySizeBytes").GetInt64(),
            Is.EqualTo(DiscTrack1.Length + DiscCue.Length + DiscTrack2.Length), "the primary release with its files, not the Europe one");
        var tetrisRelease = (await GetJson($"/api/games/{tetris.GetProperty("id").GetGuid()}")).GetProperty("roms")[0];
        Assert.That(tetrisRelease.GetProperty("files").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(tetrisRelease.GetProperty("totalSizeBytes").GetInt64(), Is.EqualTo(TetrisWorld.Length));
    }

    [Test]
    public async Task SetFileDownload_StreamsByFileName_WithRanges_AndAnotherReleasesFileIs404()
    {
        var game = await SetGame();
        var detail = await GetJson($"/api/games/{game.GameId}");
        var usa = Release(detail, "Disc (USA) (Track 1).bin");
        var track2Url = usa.GetProperty("files")[1].GetProperty("downloadUrl").GetString()!;
        var europeFileId = Release(detail, "Disc (Europe) (Track 1).bin").GetProperty("files")[0].GetProperty("id").GetInt64();

        using var response = await _user.GetAsync(track2Url);
        using var partial = await _user.SendAsync(HttpMethod.Get, track2Url, null, sendXsrf: false,
            headers => headers.Range = new RangeHeaderValue(10, 19));
        using var crossed = await _user.GetAsync($"/api/games/{game.GameId}/roms/{usa.GetProperty("id").GetInt64()}/files/{europeFileId}/download");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar, Is.EqualTo("Disc (USA) (Track 2).bin"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(DiscTrack2));
        Assert.That(partial.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
        Assert.That(await partial.Content.ReadAsByteArrayAsync(), Is.EqualTo(DiscTrack2[10..20]));
        Assert.That(crossed.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
