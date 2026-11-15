using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Bundles;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Bundle Creator B7: ticket issue, status and the file stream end to end, against MariaDB and real files.</summary>
[Category("Integration")]
public class BundleDownloadEndpointsTests
{
    private static readonly byte[] Track1 = [.. Enumerable.Range(0, 3000).Select(i => (byte)i)];
    private static readonly byte[] Cue = "FILE \"Disc (USA) (Track 1).bin\" BINARY\n"u8.ToArray();
    private static readonly byte[] Track2 = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);

    private ApiFactory _factory = null!;
    private AuthClient _alice = null!;
    private AuthClient _bob = null!;

    // A host per test: tickets are in-memory state that ApiFactory.ResetAsync doesn't restore.
    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _alice = await SignInAsync("alice");
        _bob = await SignInAsync("bob");
    }

    [TearDown]
    public void TearDown()
    {
        _alice?.Dispose();
        _bob?.Dispose();
        _factory.Dispose();
    }

    private async Task<AuthClient> SignInAsync(string username)
    {
        var client = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, username);
        (await client.LoginAsync(username, TestUsers.Password)).Dispose();
        return client;
    }

    private string Folder => Path.Combine(_factory.Root, "roms", "extset", "D");

    private static InfoJsonRomFile SetFile(string name, byte[] bytes)
    {
        var m = Rom(name, bytes).Metadata;
        return new InfoJsonRomFile(name, name, name, m.SizeBytes, m.Crc32, m.Md5, m.Sha1);
    }

    /// <summary>"Disc" with its main track, cue and an 8 MiB Track 2 on disk.</summary>
    private async Task<InfoJsonGame> DiscAsync()
    {
        var game = Game("Disc", Rom("Disc (USA) (Track 1).bin", Track1) with
        {
            ExternalFileName = "Disc (USA) (Track 1).bin",
            Files = [SetFile("Disc (USA).cue", Cue), SetFile("Disc (USA) (Track 2).bin", Track2)],
        }) with { System = ExternalSetTestHandler.SystemId };
        await _factory.Services.GetRequiredService<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, "extset/D/Disc.zip", 100, When.UtcDateTime), default);
        Directory.CreateDirectory(Folder);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "Disc (USA) (Track 1).bin"), Track1);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "Disc (USA).cue"), Cue);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "Disc (USA) (Track 2).bin"), Track2);
        return game;
    }

    private static object MainItem(InfoJsonGame game) => new { gameId = game.GameId, releaseSha1 = game.Roms[0].Metadata.Sha1, member = (object?)null };

    private static object FileItem(InfoJsonGame game, int file)
    {
        var f = game.Roms[0].Files![file];
        return new { gameId = game.GameId, releaseSha1 = game.Roms[0].Metadata.Sha1, member = new { sha1 = f.Sha1, fileName = f.FileName } };
    }

    private static async Task<(string Ticket, string FileUrl)> IssueAsync(AuthClient client, object item)
    {
        using var response = await client.PostAsync("/api/bundle-downloads", item);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created), await response.Content.ReadAsStringAsync());
        Assert.That(response.Headers.CacheControl?.ToString(), Is.EqualTo("no-store, private").Or.EqualTo("private, no-store"));
        var body = await AuthClient.JsonAsync(response);
        return (body.GetProperty("ticket").GetString()!, body.GetProperty("fileUrl").GetString()!);
    }

    private static async Task<JsonElement?> StatusAsync(AuthClient client, string ticket)
    {
        using var response = await client.GetAsync($"/api/bundle-downloads/{ticket}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await AuthClient.JsonAsync(response);
    }

    /// <summary>Polls like the SPA until the ticket is terminal (the server marks it after the response completes).</summary>
    private static async Task<(string State, string? ErrorCode)> TerminalAsync(AuthClient client, string ticket)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var status = (await StatusAsync(client, ticket))!.Value;
            var state = status.GetProperty("state").GetString()!;
            if (state is not ("pending" or "sending"))
                return (state, status.GetProperty("errorCode").GetString());
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Ticket still {state}.");
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task AFullStream_SendsTheWholeFile_ThenIsSent_AndFreesTheSlot()
    {
        var game = await DiscAsync();
        var (ticket, fileUrl) = await IssueAsync(_alice, FileItem(game, 1));
        Assert.That(fileUrl, Is.EqualTo($"/api/bundle-downloads/{ticket}/file"));
        Assert.That((await StatusAsync(_alice, ticket))!.Value.GetProperty("state").GetString(), Is.EqualTo("pending"));

        using var response = await _alice.SendAsync(HttpMethod.Get, fileUrl, null, sendXsrf: false, headers => headers.Range = new RangeHeaderValue(0, 9));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "ranges are ignored");
        Assert.That(response.Content.Headers.ContentDisposition?.DispositionType, Is.EqualTo("attachment"));
        Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar, Is.EqualTo("Disc (USA) (Track 2).bin"));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(Track2.Length));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(response.Headers.AcceptRanges, Is.Empty);
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Track2));
        Assert.That(await TerminalAsync(_alice, ticket), Is.EqualTo(("sent", (string?)null)));
        await IssueAsync(_alice, MainItem(game));
    }

    [Test]
    public async Task AMainFile_StreamsItsBytes_UnderItsName()
    {
        var game = await DiscAsync();
        var (ticket, fileUrl) = await IssueAsync(_alice, MainItem(game));

        using var response = await _alice.GetAsync(fileUrl);

        Assert.That(response.Content.Headers.ContentDisposition?.FileNameStar, Is.EqualTo("Disc (USA) (Track 1).bin"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Track1));
        Assert.That((await TerminalAsync(_alice, ticket)).State, Is.EqualTo("sent"));
    }

    [Test]
    public async Task ATicket_IsClaimedOnce()
    {
        var game = await DiscAsync();
        var (ticket, fileUrl) = await IssueAsync(_alice, FileItem(game, 0));

        using (var first = await _alice.GetAsync(fileUrl))
            Assert.That(await first.Content.ReadAsByteArrayAsync(), Is.EqualTo(Cue));
        using var second = await _alice.GetAsync(fileUrl);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(second), Is.EqualTo("BundleDownloadConsumed"));
        Assert.That((await TerminalAsync(_alice, ticket)).State, Is.EqualTo("sent"));
    }

    [Test]
    public async Task AnotherUsersTicket_Is404_ForStatusAndFile_AndStaysUsable()
    {
        var game = await DiscAsync();
        var (ticket, fileUrl) = await IssueAsync(_alice, FileItem(game, 0));

        using (var file = await _bob.GetAsync(fileUrl))
            Assert.That(file.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await StatusAsync(_bob, ticket), Is.Null);
        using (var unknown = await _alice.GetAsync($"/api/bundle-downloads/{new string('0', 64)}/file"))
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using var mine = await _alice.GetAsync(fileUrl);
        Assert.That(mine.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Issue_ForAnUnavailableFile_Is404_AndBusyWhilePending()
    {
        var game = await DiscAsync();
        using (var unknown = await _alice.PostAsync("/api/bundle-downloads", new { gameId = Guid.NewGuid(), releaseSha1 = new string('a', 40) }))
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        await IssueAsync(_alice, FileItem(game, 0));
        using var busy = await _alice.PostAsync("/api/bundle-downloads", FileItem(game, 1));

        Assert.That(await AuthClient.CodeAsync(busy), Is.EqualTo("BundleDownloadBusy"));
        await IssueAsync(_bob, FileItem(game, 1));
    }

    [Test]
    public async Task AFileGoneAfterIssue_Is404_AndTheTicketFails()
    {
        var game = await DiscAsync();
        var (ticket, fileUrl) = await IssueAsync(_alice, FileItem(game, 1));
        File.Delete(Path.Combine(Folder, "Disc (USA) (Track 2).bin"));

        using var response = await _alice.GetAsync(fileUrl);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await TerminalAsync(_alice, ticket), Is.EqualTo(("failed", (string?)"OpenFailed")));
        await IssueAsync(_alice, FileItem(game, 0));
    }

    [Test]
    public async Task AReleaseRemovedAfterIssue_Is404_Unavailable()
    {
        var game = await DiscAsync();
        var (ticket, fileUrl) = await IssueAsync(_alice, FileItem(game, 1));
        await _factory.Services.GetRequiredService<ILibraryMirror>().ApplyAsync([], [game.GameId], default);

        using var response = await _alice.GetAsync(fileUrl);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await TerminalAsync(_alice, ticket), Is.EqualTo(("failed", (string?)"Unavailable")));
    }

    [Test]
    public async Task AnAbortedStream_IsFailed_NeverSent_AndFreesTheSlot()
    {
        var game = await DiscAsync();
        var (ticket, fileUrl) = await IssueAsync(_alice, FileItem(game, 1));

        using (var response = await _alice.SendAsync(HttpMethod.Get, fileUrl, null, sendXsrf: false, completion: HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            await using var body = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[4096];
            Assert.That(await body.ReadAsync(buffer), Is.GreaterThan(0));
        } // the client walks away with most of the 8 MiB unread

        var (state, errorCode) = await TerminalAsync(_alice, ticket);
        Assert.That((state, errorCode), Is.EqualTo(("failed", (string?)"Aborted")));
        await IssueAsync(_alice, FileItem(game, 0));
    }

    [Test]
    public async Task Status_AndTicketUrls_AreNeverCached()
    {
        var game = await DiscAsync();
        var (ticket, _) = await IssueAsync(_alice, FileItem(game, 0));

        using var response = await _alice.GetAsync($"/api/bundle-downloads/{ticket}");

        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(response.Headers.CacheControl?.Private, Is.True);
        Assert.That((await AuthClient.JsonAsync(response)).ToString(), Is.EqualTo("""{"state":"pending","errorCode":null}"""));
    }

    [Test]
    public async Task SignedOut_Is401_ForEveryTicketRoute()
    {
        var game = await DiscAsync();
        var (ticket, fileUrl) = await IssueAsync(_alice, FileItem(game, 0));
        using var anonymous = await AuthClient.CreateAsync(_factory);

        foreach (var url in new[] { $"/api/bundle-downloads/{ticket}", fileUrl })
            using (var response = await anonymous.GetAsync(url))
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), url);
        using var issue = await anonymous.PostAsync("/api/bundle-downloads", FileItem(game, 0));
        Assert.That(issue.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_factory.Services.GetRequiredService<BundleDownloadTickets>().Status(ticket, 0), Is.Null);
    }
}
