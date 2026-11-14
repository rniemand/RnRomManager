using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Library;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Play B2–B3: <c>/play-disc</c> for PlayStation releases, against real files under <c>Paths:Roms</c>.</summary>
public partial class LibraryEndpointsTests
{
    private static readonly byte[] PsxTrack1 = [.. Enumerable.Range(0, 2352 * 3).Select(i => (byte)(i * 7))];
    private static readonly byte[] PsxTrack2 = [.. Enumerable.Range(0, 2352 * 2).Select(i => (byte)(i * 13 + 1))];
    private static readonly byte[] PsxDisc2Track1 = [.. Enumerable.Range(0, 2352).Select(i => (byte)(i * 3 + 5))];

    private static byte[] PsxCue(string track1, string track2) => Encoding.UTF8.GetBytes(
        $"REM COMMENT test\r\nFILE \"{track1}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n" +
        $"FILE \"{track2}\" BINARY\r\n  TRACK 02 AUDIO\r\n    INDEX 00 00:00:00\r\n    INDEX 01 00:00:01\r\n");

    private static InfoJsonRom PsxSet(string disc, byte[] track1, byte[] cue, byte[] track2, bool isPrimary = true) =>
        TestLibrary.Rom($"{disc} (Track 1).bin", track1, isPrimary) with
        {
            ExternalFileName = $"{disc} (Track 1).bin",
            Files = [SetFile($"{disc}.cue", cue), SetFile($"{disc} (Track 2).bin", track2)],
        };

    private static InfoJsonRom PsxSolo(string name, byte[] bytes, bool isPrimary = true) =>
        TestLibrary.Rom(name, bytes, isPrimary) with { ExternalFileName = name };

    /// <summary>Mirrors a PlayStation game at <c>psx/D/&lt;title&gt;.zip</c> and writes <paramref name="onDisk"/> beside it.</summary>
    private Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> PsxGame(string title, InfoJsonRom[] roms,
        params (string Name, byte[] Bytes)[] onDisk) => DiscGame(PlayDisc.SystemId, title, roms, onDisk);

    /// <summary>Mirrors a disc game of <paramref name="system"/> at <c>&lt;system&gt;/D/&lt;title&gt;.zip</c>.</summary>
    private async Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> DiscGame(string system, string title,
        InfoJsonRom[] roms, params (string Name, byte[] Bytes)[] onDisk)
    {
        var game = TestLibrary.Game(title, roms) with { System = system };
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, $"{system}/D/{title}.zip", 100, DateTime.UtcNow), default);
        var folder = RomsFolder(system, "D");
        foreach (var (name, bytes) in onDisk)
            await File.WriteAllBytesAsync(Path.Combine(folder, name), bytes);
        var detail = await GetJson($"/api/games/{game.GameId}");
        var ids = roms.Select(r => Release(detail, r.FileName).GetProperty("id").GetInt64()).ToList();
        return (game.GameId, folder, ids);
    }

    private async Task<(Guid Id, string Folder, long RomId)> PsxUsaSet()
    {
        var cue = PsxCue("Disc (USA) (Track 1).bin", "Disc (USA) (Track 2).bin");
        var (id, folder, ids) = await PsxGame("Disc", [PsxSet("Disc (USA)", PsxTrack1, cue, PsxTrack2)],
            ("Disc (USA) (Track 1).bin", PsxTrack1), ("Disc (USA).cue", cue), ("Disc (USA) (Track 2).bin", PsxTrack2));
        return (id, folder, ids[0]);
    }

    private static string PlayDiscUrl(Guid id, long romId) => $"/api/games/{id}/roms/{romId}/play-disc";

    private async Task<string> ProblemCode(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.That(response.StatusCode, Is.EqualTo(status));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True, "endpoint errors are not cached");
        return (await AuthClient.CodeAsync(response))!;
    }

    private static void AssertNotOpen(string path) =>
        Assert.DoesNotThrow(() => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose(), path);

    [Test]
    public async Task PlayDisc_ASet_IsAnUncompressedZip_WithThePlaybackCueAndAliasedTracks()
    {
        var (id, folder, romId) = await PsxUsaSet();

        using var response = await _user.GetAsync(PlayDiscUrl(id, romId));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Disc (USA).cue", "track-01.bin", "track-02.bin" }));
        Assert.That(zip.Entries.Select(e => e.CompressedLength == e.Length), Has.All.True, "stored, not deflated");
        Assert.That(ReadEntry(zip, "Disc (USA).cue"), Is.EqualTo(Encoding.UTF8.GetBytes(
            "FILE \"track-01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "FILE \"track-02.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 00 00:00:00\n    INDEX 01 00:00:01\n")));
        Assert.That(ReadEntry(zip, "track-01.bin"), Is.EqualTo(PsxTrack1));
        Assert.That(ReadEntry(zip, "track-02.bin"), Is.EqualTo(PsxTrack2));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(folder, "Disc (USA).cue")),
            Is.EqualTo(PsxCue("Disc (USA) (Track 1).bin", "Disc (USA) (Track 2).bin")), "the stored cue is unchanged");
        foreach (var name in new[] { "Disc (USA) (Track 1).bin", "Disc (USA).cue", "Disc (USA) (Track 2).bin" })
            AssertNotOpen(Path.Combine(folder, name));
        Assert.That(Directory.GetFiles(folder), Has.Length.EqualTo(3), "no temporary files");
    }

    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [TestCase("Solo (USA).bin")]
    [TestCase("Solo (USA).iso")]
    public async Task PlayDisc_AStandaloneImage_StreamsUnchanged_EvenForARangeRequest(string name)
    {
        var (id, folder, ids) = await PsxGame("Solo", [PsxSolo(name, PsxTrack1)], (name, PsxTrack1));

        using var response = await _user.SendAsync(HttpMethod.Get, PlayDiscUrl(id, ids[0]), null, sendXsrf: false,
            headers => headers.Range = new RangeHeaderValue(10, 19));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(PsxTrack1.Length));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(PsxTrack1));
        AssertNotOpen(Path.Combine(folder, name));
    }

    [Test]
    public async Task PlayDisc_SeparateDiscsOfOneGame_EachStreamTheirOwnSet()
    {
        var cue1 = PsxCue("Epic (USA) (Disc 1) (Track 1).bin", "Epic (USA) (Disc 1) (Track 2).bin");
        var cue2 = PsxCue("Epic (USA) (Disc 2) (Track 1).bin", "Epic (USA) (Disc 2) (Track 2).bin");
        var track2Disc2 = PsxTrack2.Reverse().ToArray();
        var (id, _, ids) = await PsxGame("Epic",
            [PsxSet("Epic (USA) (Disc 1)", PsxTrack1, cue1, PsxTrack2), PsxSet("Epic (USA) (Disc 2)", PsxDisc2Track1, cue2, track2Disc2, isPrimary: false)],
            ("Epic (USA) (Disc 1) (Track 1).bin", PsxTrack1), ("Epic (USA) (Disc 1).cue", cue1), ("Epic (USA) (Disc 1) (Track 2).bin", PsxTrack2),
            ("Epic (USA) (Disc 2) (Track 1).bin", PsxDisc2Track1), ("Epic (USA) (Disc 2).cue", cue2), ("Epic (USA) (Disc 2) (Track 2).bin", track2Disc2));

        using var disc2 = await _user.GetAsync(PlayDiscUrl(id, ids[1]));

        await using var body = new MemoryStream(await disc2.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Epic (USA) (Disc 2).cue", "track-01.bin", "track-02.bin" }));
        Assert.That(ReadEntry(zip, "track-01.bin"), Is.EqualTo(PsxDisc2Track1));
        Assert.That(ReadEntry(zip, "track-02.bin"), Is.EqualTo(track2Disc2));
    }

    [Test]
    public async Task PlayDisc_AMalformedCue_Is409InvalidPlayDisc_AndReleasesTheFiles()
    {
        var cue = "FILE \"Bad (USA) (Track 1).bin\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\nFILE \"Bad (USA) (Track 2).bin\" BINARY\n"u8.ToArray();
        var (id, folder, ids) = await PsxGame("Bad", [PsxSet("Bad (USA)", PsxTrack1, cue, PsxTrack2)],
            ("Bad (USA) (Track 1).bin", PsxTrack1), ("Bad (USA).cue", cue), ("Bad (USA) (Track 2).bin", PsxTrack2));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(await ProblemCode(response, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.InvalidPlayDisc));
        foreach (var name in Directory.GetFiles(folder))
            AssertNotOpen(name);
    }

    [Test]
    public async Task PlayDisc_ACueThatMissesAStoredTrack_Is409()
    {
        var cue = "FILE \"Half (USA) (Track 1).bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"u8.ToArray();
        var (id, _, ids) = await PsxGame("Half", [PsxSet("Half (USA)", PsxTrack1, cue, PsxTrack2)],
            ("Half (USA) (Track 1).bin", PsxTrack1), ("Half (USA).cue", cue), ("Half (USA) (Track 2).bin", PsxTrack2));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(await ProblemCode(response, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.InvalidPlayDisc));
    }

    [Test]
    public async Task PlayDisc_WrongGame_Release_OrSystem_Is404()
    {
        var (id, _, romId) = await PsxUsaSet();
        var gbId = await Ingest("t1.gb", TetrisWorld);
        var gbRomId = (await GetJson($"/api/games/{gbId}")).GetProperty("roms")[0].GetProperty("id").GetInt64();
        var extset = await SetGame();
        var extsetRomId = Release(await GetJson($"/api/games/{extset.GameId}"), "Disc (USA) (Track 1).bin").GetProperty("id").GetInt64();

        foreach (var url in new[] { PlayDiscUrl(gbId, romId), PlayDiscUrl(id, romId + 1000), PlayDiscUrl(gbId, gbRomId), PlayDiscUrl(extset.GameId, extsetRomId) })
        {
            using var response = await _user.GetAsync(url);
            Assert.That(await ProblemCode(response, HttpStatusCode.NotFound), Is.EqualTo("NotFound"), url);
        }
    }

    [Test]
    public async Task PlayDisc_AMissingOrUnsafeMember_Is404()
    {
        var (id, folder, romId) = await PsxUsaSet();
        File.Delete(Path.Combine(folder, "Disc (USA) (Track 2).bin"));
        Directory.CreateDirectory(Path.Combine(folder, "Disc (USA) (Track 2).bin")); // not a regular file

        using var response = await _user.GetAsync(PlayDiscUrl(id, romId));

        Assert.That(await ProblemCode(response, HttpStatusCode.NotFound), Is.EqualTo("NotFound"));
        AssertNotOpen(Path.Combine(folder, "Disc (USA) (Track 1).bin"));
        Directory.Delete(Path.Combine(folder, "Disc (USA) (Track 2).bin"));

        using var missing = await _user.GetAsync(PlayDiscUrl(id, romId));
        Assert.That(await ProblemCode(missing, HttpStatusCode.NotFound), Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task PlayDisc_AMemberAnotherGameInTheFolderClaims_Is404()
    {
        var (id, _, romId) = await PsxUsaSet();
        await PsxGame("Disc Clash", [PsxSolo("DISC (USA) (TRACK 2).BIN", PsxDisc2Track1)]);

        using var response = await _user.GetAsync(PlayDiscUrl(id, romId));

        Assert.That(await ProblemCode(response, HttpStatusCode.NotFound), Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task PlayDisc_AMemberAnotherReleaseOfTheSameGameClaims_Is404()
    {
        var cue = PsxCue("Twin (USA) (Track 1).bin", "Twin (USA) (Track 2).bin");
        var (id, _, ids) = await PsxGame("Twin",
            [PsxSet("Twin (USA)", PsxTrack1, cue, PsxTrack2), PsxSolo("twin (usa) (track 1).bin", PsxDisc2Track1, isPrimary: false)],
            ("Twin (USA) (Track 1).bin", PsxTrack1), ("Twin (USA).cue", cue), ("Twin (USA) (Track 2).bin", PsxTrack2));

        using var set = await _user.GetAsync(PlayDiscUrl(id, ids[0]));
        using var solo = await _user.GetAsync(PlayDiscUrl(id, ids[1]));

        Assert.That(await ProblemCode(set, HttpStatusCode.NotFound), Is.EqualTo("NotFound"));
        Assert.That(await ProblemCode(solo, HttpStatusCode.NotFound), Is.EqualTo("NotFound"), "a main file's claim has no file id, and still collides");
    }

    [Test]
    public async Task PlayDisc_AMemberNameThatDiffersFromItsExternalName_Is409()
    {
        var cue = PsxCue("Named (USA) (Track 1).bin", "Named (USA) (Track 2).bin");
        var rom = PsxSet("Named (USA)", PsxTrack1, cue, PsxTrack2) with { FileName = "Named (USA) (Track 1) [renamed].bin" };
        var game = TestLibrary.Game("Named", rom) with { System = PlayDisc.SystemId };
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, "psx/D/Named.zip", 100, DateTime.UtcNow), default);
        var romId = (await GetJson($"/api/games/{game.GameId}")).GetProperty("roms")[0].GetProperty("id").GetInt64();

        using var response = await _user.GetAsync(PlayDiscUrl(game.GameId, romId));

        Assert.That(await ProblemCode(response, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.InvalidPlayDisc));
    }

    [Test]
    public async Task PlayDisc_AnEmptyMember_Is409()
    {
        var (id, folder, romId) = await PsxUsaSet();
        await File.WriteAllBytesAsync(Path.Combine(folder, "Disc (USA) (Track 2).bin"), []);

        using var response = await _user.GetAsync(PlayDiscUrl(id, romId));

        Assert.That(await ProblemCode(response, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.InvalidPlayDisc));
    }

    [Test]
    public async Task PlayDisc_OverOneGiB_Is409PlayDiscTooLarge()
    {
        var (id, folder, romId) = await PsxUsaSet();
        await using (var grown = new FileStream(Path.Combine(folder, "Disc (USA) (Track 2).bin"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes - PsxTrack1.Length); // with the cue, one GiB plus a few bytes

        using var response = await _user.GetAsync(PlayDiscUrl(id, romId));

        Assert.That(await ProblemCode(response, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.PlayDiscTooLarge));
        AssertNotOpen(Path.Combine(folder, "Disc (USA) (Track 2).bin"));
    }

    [Test]
    public async Task PlayDisc_AnonymousIs401_AndAForcedPasswordChangeIs403()
    {
        var (id, _, romId) = await PsxUsaSet();
        using var anonymous = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "forced", mustChangePassword: true);
        using var forced = await AuthClient.CreateAsync(_factory);
        (await forced.LoginAsync("forced", TestUsers.Password)).Dispose();

        using var asAnonymous = await anonymous.GetAsync(PlayDiscUrl(id, romId));
        using var asForced = await forced.GetAsync(PlayDiscUrl(id, romId));

        Assert.That(asAnonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(asForced.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await AuthClient.CodeAsync(asForced), Is.EqualTo("PasswordChangeRequired"));
    }

    // PSP B2: a PlayStation Portable release streams its standalone .iso, bounded at 2 GiB inclusive.

    private static readonly byte[] PspIso = Umd(3);

    /// <summary>A small synthetic image; each <paramref name="seed"/> gives distinct bytes (stored SHA-1s are unique).</summary>
    private static byte[] Umd(int seed) => [.. Enumerable.Range(0, 2048 * 5).Select(i => (byte)(i * 11 + seed))];

    private Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> PspGame(string title, InfoJsonRom[] roms,
        params (string Name, byte[] Bytes)[] onDisk) => DiscGame(PlayDisc.PspSystemId, title, roms, onDisk);

    private async Task<(Guid Id, string Folder, long RomId)> PspUmd(string title = "Umd", int seed = 3)
    {
        var name = $"{title} (USA).iso";
        var bytes = Umd(seed);
        var (id, folder, ids) = await PspGame(title, [PsxSolo(name, bytes)], (name, bytes));
        return (id, folder, ids[0]);
    }

    [Test]
    public async Task PlayDisc_APspIso_StreamsByteForByte_EvenForARangeRequest()
    {
        var (id, folder, romId) = await PspUmd();

        using var response = await _user.SendAsync(HttpMethod.Get, PlayDiscUrl(id, romId), null, sendXsrf: false,
            headers => headers.Range = new RangeHeaderValue(10, 19));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(PspIso.Length));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(response.Headers.AcceptRanges, Is.EqualTo(new[] { "none" }));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(PspIso));
        AssertNotOpen(Path.Combine(folder, "Umd (USA).iso"));
    }

    [Test]
    public async Task PlayDisc_APspIsoOfExactlyTwoGiB_Streams_AndOneByteMoreIs409PlayDiscTooLarge()
    {
        var (atId, atFolder, atRom) = await PspUmd("Big", 5);
        var (overId, overFolder, overRom) = await PspUmd("Bigger", 7);
        // Lengths are set on disk only: the stored sizes stay the small originals.
        await using (var grown = new FileStream(Path.Combine(atFolder, "Big (USA).iso"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.PspMaxBytes);
        await using (var grown = new FileStream(Path.Combine(overFolder, "Bigger (USA).iso"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.PspMaxBytes + 1);

        using (var atLimit = await _user.SendAsync(HttpMethod.Get, PlayDiscUrl(atId, atRom), null, sendXsrf: false,
            completion: HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.That(atLimit.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(atLimit.Content.Headers.ContentLength, Is.EqualTo(PlayDisc.PspMaxBytes));
        } // disposed with the body unread: the stream is cancelled

        using var over = await _user.GetAsync(PlayDiscUrl(overId, overRom));
        Assert.That(over.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await AuthClient.JsonAsync(over);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo(PlayDisc.PlayDiscTooLarge));
        Assert.That(problem.GetProperty("title").GetString(), Does.Contain("2 GiB"));
        AssertNotOpen(Path.Combine(overFolder, "Bigger (USA).iso"));
    }

    [Test]
    public async Task PlayDisc_APspReleaseThatIsNotOneIso_OrIsEmpty_OrMisnamed_Is409InvalidPlayDisc()
    {
        var bin = await PspGame("Binary", [PsxSolo("Binary (USA).bin", Umd(10))], ("Binary (USA).bin", Umd(10)));
        var set = await PspGame("Setted",
            [PsxSolo("Setted (USA).iso", Umd(11)) with { Files = [SetFile("Setted (USA).cue", Umd(12))] }],
            ("Setted (USA).iso", Umd(11)), ("Setted (USA).cue", Umd(12)));
        var empty = await PspGame("Hollow", [PsxSolo("Hollow (USA).iso", Umd(13))], ("Hollow (USA).iso", []));
        var misnamed = await PspGame("Renamed",
            [PsxSolo("Renamed (USA).iso", Umd(14)) with { FileName = "Renamed (USA) [b].iso" }], ("Renamed (USA).iso", Umd(14)));

        foreach (var (id, _, ids) in new[] { bin, set, empty, misnamed })
        {
            using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));
            Assert.That(await ProblemCode(response, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.InvalidPlayDisc), id.ToString());
        }
    }

    [Test]
    public async Task PlayDisc_APspIsoThatIsMissing_NotAFile_OrClaimedByAnotherRelease_Is404()
    {
        var (missingId, missingFolder, missingRom) = await PspUmd("Gone", 20);
        File.Delete(Path.Combine(missingFolder, "Gone (USA).iso"));
        var (dirId, dirFolder, dirRom) = await PspUmd("Folder", 21);
        File.Delete(Path.Combine(dirFolder, "Folder (USA).iso"));
        Directory.CreateDirectory(Path.Combine(dirFolder, "Folder (USA).iso"));
        var (twinId, _, twinRoms) = await PspGame("Twin Umd",
            [PsxSolo("Twin Umd (USA).iso", Umd(22)), PsxSolo("TWIN UMD (USA).ISO", Umd(23), isPrimary: false)],
            ("Twin Umd (USA).iso", Umd(22)));
        var (psxId, _, psxRom) = await PsxUsaSet();
        var (pspId, _, pspRom) = await PspUmd("Mixed", 24);

        foreach (var url in new[]
        {
            PlayDiscUrl(missingId, missingRom), PlayDiscUrl(dirId, dirRom), PlayDiscUrl(twinId, twinRoms[0]),
            PlayDiscUrl(pspId, psxRom), PlayDiscUrl(psxId, pspRom), PlayDiscUrl(pspId, pspRom + 1000),
        })
        {
            using var response = await _user.GetAsync(url);
            Assert.That(await ProblemCode(response, HttpStatusCode.NotFound), Is.EqualTo("NotFound"), url);
        }
        Directory.Delete(Path.Combine(dirFolder, "Folder (USA).iso"));
    }

    [Test]
    public async Task PlayDisc_APspIso_AnonymousIs401_AndAForcedPasswordChangeIs403()
    {
        var (id, _, romId) = await PspUmd("Locked", 25);
        using var anonymous = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "forced-psp", mustChangePassword: true);
        using var forced = await AuthClient.CreateAsync(_factory);
        (await forced.LoginAsync("forced-psp", TestUsers.Password)).Dispose();

        using var asAnonymous = await anonymous.GetAsync(PlayDiscUrl(id, romId));
        using var asForced = await forced.GetAsync(PlayDiscUrl(id, romId));

        Assert.That(asAnonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(asForced.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await AuthClient.CodeAsync(asForced), Is.EqualTo("PasswordChangeRequired"));
    }
}
