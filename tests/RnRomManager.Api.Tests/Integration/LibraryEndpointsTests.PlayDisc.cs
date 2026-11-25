using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Play B2–B3: <c>/play-disc</c> for PlayStation (PSP B2, Saturn B5) releases, against real files under <c>Paths:Roms</c>.</summary>
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

        // Headers only, so HttpClient doesn't fill ContentLength in from a buffered body.
        using var response = await _user.SendAsync(HttpMethod.Get, PlayDiscUrl(id, romId), null, sendXsrf: false,
            completion: HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(response.Content.Headers.ContentLength, Is.Null, "checked before the body is buffered");
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Disc (USA).cue", "track-01.bin", "track-02.bin" }));
        Assert.That(zip.Entries.Select(e => e.CompressedLength == e.Length), Has.All.True, "stored, not deflated");
        Assert.That(ReadEntry(zip, "Disc (USA).cue"), Is.EqualTo(Encoding.UTF8.GetBytes(
            "FILE \"track-01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n" +
            "FILE \"track-02.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 00 00:00:00\n    INDEX 01 00:00:01\n")));
        Assert.That(ReadEntry(zip, "track-01.bin"), Is.EqualTo(PsxTrack1));
        Assert.That(ReadEntry(zip, "track-02.bin"), Is.EqualTo(PsxTrack2));
        var payload = ReadEntry(zip, "Disc (USA).cue").Length + PsxTrack1.Length + PsxTrack2.Length;
        Assert.That(response.Headers.GetValues("X-Content-Bytes"), Is.EqualTo(new[] { payload.ToString(CultureInfo.InvariantCulture) }));
        Assert.That(payload, Is.LessThan(body.Length), "an estimate below the ZIP's size");
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
    [TestCase("Solo (USA).img")]
    public async Task PlayDisc_AStandaloneImage_StreamsUnchanged_EvenForARangeRequest(string name)
    {
        var (id, folder, ids) = await PsxGame("Solo", [PsxSolo(name, PsxTrack1)], (name, PsxTrack1));

        using var response = await _user.SendAsync(HttpMethod.Get, PlayDiscUrl(id, ids[0]), null, sendXsrf: false,
            headers => headers.Range = new RangeHeaderValue(10, 19));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(PsxTrack1.Length));
        Assert.That(response.Headers.Contains("X-Content-Bytes"), Is.False);
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
    public async Task PlayDisc_OverTwoGiB_Is409PlayDiscTooLarge()
    {
        var (id, folder, romId) = await PsxUsaSet();
        await using (var grown = new FileStream(Path.Combine(folder, "Disc (USA) (Track 2).bin"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes - PsxTrack1.Length); // with the cue, two GiB plus a few bytes

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

    [TestCase("Packed (USA).cso")]
    [TestCase("Packed (USA).PBP")]
    [TestCase("Packed (USA).chd")]
    public async Task PlayDisc_APspCsoPbpOrChd_StreamsUnchanged(string name)
    {
        var bytes = Umd(30);
        var (id, _, ids) = await PspGame("Packed", [PsxSolo(name, bytes)], (name, bytes));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));
    }

    [Test]
    public async Task PlayDisc_APspIsoOfExactlyTwoGiB_Streams_AndOneByteMoreIs409PlayDiscTooLarge()
    {
        var (atId, atFolder, atRom) = await PspUmd("Big", 5);
        var (overId, overFolder, overRom) = await PspUmd("Bigger", 7);
        // Lengths are set on disk only: the stored sizes stay the small originals.
        await using (var grown = new FileStream(Path.Combine(atFolder, "Big (USA).iso"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes);
        await using (var grown = new FileStream(Path.Combine(overFolder, "Bigger (USA).iso"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes + 1);

        using (var atLimit = await _user.SendAsync(HttpMethod.Get, PlayDiscUrl(atId, atRom), null, sendXsrf: false,
            completion: HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.That(atLimit.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(atLimit.Content.Headers.ContentLength, Is.EqualTo(PlayDisc.MaxBytes));
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

    // Saturn B5: PlayStation's shapes and its 2 GiB inclusive cap. Browser play format gaps N1: a lone .nrg plays as a generated cue.

    private Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> SaturnGame(string title, InfoJsonRom[] roms,
        params (string Name, byte[] Bytes)[] onDisk) => DiscGame(PlayDisc.SaturnSystemId, title, roms, onDisk);

    [Test]
    public async Task PlayDisc_ASaturnSingleBin_StreamsUnchanged()
    {
        var (id, folder, ids) = await SaturnGame("Sky Solo", [PsxSolo("Sky Solo (USA).bin", PsxTrack1)], ("Sky Solo (USA).bin", PsxTrack1));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(PsxTrack1));
        AssertNotOpen(Path.Combine(folder, "Sky Solo (USA).bin"));
    }

    [Test]
    public async Task PlayDisc_ASaturnCueSet_IsAStoredZip_WithThePlaybackCue()
    {
        var cue = PsxCue("Sky Rally (USA) (Track 1).bin", "Sky Rally (USA) (Track 2).bin");
        var (id, _, ids) = await SaturnGame("Sky Rally", [PsxSet("Sky Rally (USA)", PsxTrack1, cue, PsxTrack2)],
            ("Sky Rally (USA) (Track 1).bin", PsxTrack1), ("Sky Rally (USA).cue", cue), ("Sky Rally (USA) (Track 2).bin", PsxTrack2));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Sky Rally (USA).cue", "track-01.bin", "track-02.bin" }));
        Assert.That(zip.Entries.Select(e => e.CompressedLength == e.Length), Has.All.True, "stored, not deflated");
        Assert.That(ReadEntry(zip, "track-01.bin"), Is.EqualTo(PsxTrack1));
        Assert.That(ReadEntry(zip, "track-02.bin"), Is.EqualTo(PsxTrack2));
    }

    [Test]
    public async Task PlayDisc_ASaturnDiscOfExactlyTwoGiB_Streams_AndOneByteMoreIs409PlayDiscTooLarge()
    {
        var (atId, atFolder, atRoms) = await SaturnGame("Wide", [PsxSolo("Wide (USA).bin", Umd(30))], ("Wide (USA).bin", Umd(30)));
        var (overId, overFolder, overRoms) = await SaturnGame("Wider", [PsxSolo("Wider (USA).bin", Umd(31))], ("Wider (USA).bin", Umd(31)));
        // Lengths are set on disk only: the stored sizes stay the small originals.
        await using (var grown = new FileStream(Path.Combine(atFolder, "Wide (USA).bin"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes);
        await using (var grown = new FileStream(Path.Combine(overFolder, "Wider (USA).bin"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes + 1);

        using (var atLimit = await _user.SendAsync(HttpMethod.Get, PlayDiscUrl(atId, atRoms[0]), null, sendXsrf: false,
            completion: HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.That(atLimit.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(atLimit.Content.Headers.ContentLength, Is.EqualTo(PlayDisc.MaxBytes));
        } // disposed with the body unread: the stream is cancelled

        using var over = await _user.GetAsync(PlayDiscUrl(overId, overRoms[0]));
        Assert.That(over.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await AuthClient.JsonAsync(over);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo(PlayDisc.PlayDiscTooLarge));
        Assert.That(problem.GetProperty("title").GetString(), Does.Contain("2 GiB"));
        AssertNotOpen(Path.Combine(overFolder, "Wider (USA).bin"));
    }

    // A data track whose index 0 is past the image's start, then an audio track; the chunks and footer follow the 30 sectors of data.
    private static readonly byte[] NeroImage = SyntheticNrgBuilder.Build(
        [new NrgBlock(2 * 2352, 4 * 2352, 20 * 2352, Mode: 0x05), new NrgBlock(20 * 2352, 22 * 2352, 30 * 2352)]);

    [TestCase("Nero Play (USA).nrg")]
    [TestCase("Nero Play (USA).NRG")]
    public async Task PlayDisc_ASaturnNrg_StreamsACueAndTrack01WithoutTheFooter(string name)
    {
        var (id, folder, ids) = await SaturnGame("Nero Play", [PsxSolo(name, NeroImage)], (name, NeroImage));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        await using var body = new MemoryStream(bytes);
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Nero Play (USA).cue", "track-01.bin" }));
        Assert.That(Encoding.UTF8.GetString(ReadEntry(zip, "Nero Play (USA).cue")), Is.EqualTo(
            "FILE \"track-01.bin\" BINARY\n"
            + "  TRACK 01 MODE1/2352\n"
            + "    INDEX 00 00:00:02\n"
            + "    INDEX 01 00:00:04\n"
            + "  TRACK 02 AUDIO\n"
            + "    INDEX 00 00:00:20\n"
            + "    INDEX 01 00:00:22\n"));
        Assert.That(ReadEntry(zip, "track-01.bin"), Is.EqualTo(NeroImage[..(30 * 2352)]));
        Assert.That(response.Headers.GetValues("X-Content-Bytes").Single(),
            Is.EqualTo((30 * 2352 + zip.GetEntry("Nero Play (USA).cue")!.Length).ToString(CultureInfo.InvariantCulture)));
        AssertNotOpen(Path.Combine(folder, name));
    }

    [Test]
    public async Task PlayDisc_ASaturnNrg_WithoutAFooterOrInACueSet_Is409InvalidPlayDisc()
    {
        var lone = await SaturnGame("Nero", [PsxSolo("Nero (USA).nrg", Umd(32))], ("Nero (USA).nrg", Umd(32)));
        var cue = PsxCue("Mixed Nero (USA) (Track 1).bin", "Mixed Nero (USA) (Track 2).bin");
        var set = await SaturnGame("Mixed Nero",
            [PsxSolo("Mixed Nero (USA) (Track 1).bin", PsxTrack1) with
            {
                Files = [SetFile("Mixed Nero (USA).cue", cue), SetFile("Mixed Nero (USA).nrg", Umd(33))],
            }],
            ("Mixed Nero (USA) (Track 1).bin", PsxTrack1), ("Mixed Nero (USA).cue", cue), ("Mixed Nero (USA).nrg", Umd(33)));

        foreach (var (id, _, ids) in new[] { lone, set })
        {
            using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), id.ToString());
            var problem = await AuthClient.JsonAsync(response);
            Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo(PlayDisc.InvalidPlayDisc), id.ToString());
            if (id == lone.Id)
                Assert.That(problem.GetProperty("title").GetString(), Is.EqualTo("The disc can't be played: the .nrg has no Nero footer."));
        }
    }

    [Test]
    public async Task PlayDisc_ASaturnRom_UnderAnotherSystemsGame_Is404()
    {
        var (saturnId, _, saturnRoms) = await SaturnGame("Cross", [PsxSolo("Cross (USA).bin", Umd(34))], ("Cross (USA).bin", Umd(34)));
        var (psxId, _, psxRom) = await PsxUsaSet();

        foreach (var url in new[] { PlayDiscUrl(psxId, saturnRoms[0]), PlayDiscUrl(saturnId, psxRom) })
        {
            using var response = await _user.GetAsync(url);
            Assert.That(await ProblemCode(response, HttpStatusCode.NotFound), Is.EqualTo("NotFound"), url);
        }
    }

    // Sega CD B4: PlayStation's shapes and its 2 GiB inclusive cap.

    private Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> SegaCdGame(string title, InfoJsonRom[] roms,
        params (string Name, byte[] Bytes)[] onDisk) => DiscGame(PlayDisc.SegaCdSystemId, title, roms, onDisk);

    [TestCase("Comet Solo (USA).bin")]
    [TestCase("Comet Solo (USA).iso")]
    public async Task PlayDisc_ASegaCdStandaloneImage_StreamsUnchanged(string name)
    {
        var (id, folder, ids) = await SegaCdGame("Comet Solo", [PsxSolo(name, PsxTrack1)], (name, PsxTrack1));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(PsxTrack1));
        AssertNotOpen(Path.Combine(folder, name));
    }

    [Test]
    public async Task PlayDisc_ASegaCdCueSet_IsAStoredZip_WithThePlaybackCue()
    {
        var cue = PsxCue("Disc Comet (USA) (Track 1).bin", "Disc Comet (USA) (Track 2).bin");
        var (id, _, ids) = await SegaCdGame("Disc Comet", [PsxSet("Disc Comet (USA)", PsxTrack1, cue, PsxTrack2)],
            ("Disc Comet (USA) (Track 1).bin", PsxTrack1), ("Disc Comet (USA).cue", cue), ("Disc Comet (USA) (Track 2).bin", PsxTrack2));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Disc Comet (USA).cue", "track-01.bin", "track-02.bin" }));
        Assert.That(zip.Entries.Select(e => e.CompressedLength == e.Length), Has.All.True, "stored, not deflated");
        Assert.That(ReadEntry(zip, "track-01.bin"), Is.EqualTo(PsxTrack1));
        Assert.That(ReadEntry(zip, "track-02.bin"), Is.EqualTo(PsxTrack2));
    }

    [Test]
    public async Task PlayDisc_ASegaCdDiscOfExactlyTwoGiB_Streams_AndOneByteMoreIs409PlayDiscTooLarge()
    {
        var (atId, atFolder, atRoms) = await SegaCdGame("Wide", [PsxSolo("Wide (USA).iso", Umd(40))], ("Wide (USA).iso", Umd(40)));
        var (overId, overFolder, overRoms) = await SegaCdGame("Wider", [PsxSolo("Wider (USA).iso", Umd(41))], ("Wider (USA).iso", Umd(41)));
        // Lengths are set on disk only: the stored sizes stay the small originals.
        await using (var grown = new FileStream(Path.Combine(atFolder, "Wide (USA).iso"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes);
        await using (var grown = new FileStream(Path.Combine(overFolder, "Wider (USA).iso"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes + 1);

        using (var atLimit = await _user.SendAsync(HttpMethod.Get, PlayDiscUrl(atId, atRoms[0]), null, sendXsrf: false,
            completion: HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.That(atLimit.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(atLimit.Content.Headers.ContentLength, Is.EqualTo(PlayDisc.MaxBytes));
        } // disposed with the body unread: the stream is cancelled

        using var over = await _user.GetAsync(PlayDiscUrl(overId, overRoms[0]));
        Assert.That(over.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await AuthClient.JsonAsync(over);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo(PlayDisc.PlayDiscTooLarge));
        Assert.That(problem.GetProperty("title").GetString(), Does.Contain("2 GiB"));
        AssertNotOpen(Path.Combine(overFolder, "Wider (USA).iso"));
    }

    // CHD B4: a standalone .chd streams unchanged on PlayStation, Saturn, Sega CD and 3DO; never in a set, never on PSP.
    // A Saturn .iso is the same lone-image shape.

    [TestCase("psx", "Chd Solo (USA).chd")]
    [TestCase("psx", "Chd Solo (USA).CHD")]
    [TestCase("saturn", "Chd Solo (USA).chd")]
    [TestCase("saturn", "Chd Solo (USA).CHD")]
    [TestCase("segacd", "Chd Solo (USA).chd")]
    [TestCase("segacd", "Chd Solo (USA).CHD")]
    [TestCase("3do", "Chd Solo (USA).chd")]
    [TestCase("tgcd", "Chd Solo (Japan).chd")]
    [TestCase("tgcd", "Chd Solo (Japan).CHD")]
    [TestCase("pcfx", "Chd Solo (Japan).chd")]
    [TestCase("saturn", "Chd Solo (USA).iso")]
    [TestCase("psx", "Chd Solo (USA).pbp")]
    [TestCase("psx", "Chd Solo (USA).PBP")]
    [TestCase("psx", "Chd Solo (USA).cbn")]
    public async Task PlayDisc_AStandaloneChd_StreamsUnchanged(string system, string name)
    {
        var (id, folder, ids) = await DiscGame(system, "Chd Solo", [PsxSolo(name, PsxTrack1)], (name, PsxTrack1));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(PsxTrack1.Length));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(PsxTrack1));
        AssertNotOpen(Path.Combine(folder, name));
    }

    [Test]
    public async Task PlayDisc_AChdOfExactlyTwoGiB_Streams_AndOneByteMoreIs409PlayDiscTooLarge()
    {
        var (atId, atFolder, atRoms) = await SegaCdGame("Wide", [PsxSolo("Wide (USA).chd", Umd(50))], ("Wide (USA).chd", Umd(50)));
        var (overId, overFolder, overRoms) = await SegaCdGame("Wider", [PsxSolo("Wider (USA).chd", Umd(51))], ("Wider (USA).chd", Umd(51)));
        // Lengths are set on disk only: the stored sizes stay the small originals.
        await using (var grown = new FileStream(Path.Combine(atFolder, "Wide (USA).chd"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes);
        await using (var grown = new FileStream(Path.Combine(overFolder, "Wider (USA).chd"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes + 1);

        using (var atLimit = await _user.SendAsync(HttpMethod.Get, PlayDiscUrl(atId, atRoms[0]), null, sendXsrf: false,
            completion: HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.That(atLimit.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(atLimit.Content.Headers.ContentLength, Is.EqualTo(PlayDisc.MaxBytes));
        } // disposed with the body unread: the stream is cancelled

        using var over = await _user.GetAsync(PlayDiscUrl(overId, overRoms[0]));
        Assert.That(await ProblemCode(over, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.PlayDiscTooLarge));
        AssertNotOpen(Path.Combine(overFolder, "Wider (USA).chd"));
    }

    [Test]
    public async Task PlayDisc_AChdInACueSet_Is409InvalidPlayDisc()
    {
        var cue = PsxCue("Mixed Chd (USA) (Track 1).bin", "Mixed Chd (USA) (Track 2).bin");
        var (id, _, ids) = await SegaCdGame("Mixed Chd",
            [PsxSolo("Mixed Chd (USA) (Track 1).bin", PsxTrack1) with
            {
                Files = [SetFile("Mixed Chd (USA).cue", cue), SetFile("Mixed Chd (USA).chd", Umd(52))],
            }],
            ("Mixed Chd (USA) (Track 1).bin", PsxTrack1), ("Mixed Chd (USA).cue", cue), ("Mixed Chd (USA).chd", Umd(52)));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(await ProblemCode(response, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.InvalidPlayDisc));
    }

    [Test]
    public async Task PlayDisc_APspPair_Is409InvalidPlayDisc()
    {
        var (id, _, ids) = await PspGame("Umd Pair", [PsxSolo("Umd Pair (USA).chd", Umd(53)) with
            {
                Files = [SetFile("Umd Pair (USA).iso", Umd(52))],
            }],
            ("Umd Pair (USA).chd", Umd(53)), ("Umd Pair (USA).iso", Umd(52)));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(await ProblemCode(response, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.InvalidPlayDisc));
    }

    [Test]
    public async Task PlayDisc_ALoneMdf_NamesTheSingleFileShapes()
    {
        var (id, _, ids) = await SaturnGame("Lone Mdf", [PsxSolo("Lone Mdf (USA).mdf", Umd(54))], ("Lone Mdf (USA).mdf", Umd(54)));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await AuthClient.JsonAsync(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo(PlayDisc.InvalidPlayDisc));
        Assert.That(problem.GetProperty("title").GetString(), Does.Contain("a single-file disc must be a .bin, .iso, .chd, .img, .pbp, .cbn or .nrg"));
    }

    [Test]
    public async Task PlayDisc_ASegaCdSetWithTwoCues_Is409InvalidPlayDisc()
    {
        var cue = PsxCue("Twin Cue (USA) (Track 1).bin", "Twin Cue (USA) (Track 2).bin");
        var (id, _, ids) = await SegaCdGame("Twin Cue",
            [PsxSolo("Twin Cue (USA) (Track 1).bin", PsxTrack1) with
            {
                Files = [SetFile("Twin Cue (USA).cue", cue), SetFile("Twin Cue (USA) (Alt).cue", cue), SetFile("Twin Cue (USA) (Track 2).bin", PsxTrack2)],
            }],
            ("Twin Cue (USA) (Track 1).bin", PsxTrack1), ("Twin Cue (USA).cue", cue), ("Twin Cue (USA) (Alt).cue", cue),
            ("Twin Cue (USA) (Track 2).bin", PsxTrack2));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(await ProblemCode(response, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.InvalidPlayDisc));
    }

    // 3DO B4: Sega CD's shapes and its 2 GiB inclusive cap.

    private Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> ThreeDoGame(string title, InfoJsonRom[] roms,
        params (string Name, byte[] Bytes)[] onDisk) => DiscGame(PlayDisc.ThreeDoSystemId, title, roms, onDisk);

    [TestCase("Opal Solo (USA).bin")]
    [TestCase("Opal Solo (USA).iso")]
    public async Task PlayDisc_AThreeDoStandaloneImage_StreamsUnchanged(string name)
    {
        var (id, folder, ids) = await ThreeDoGame("Opal Solo", [PsxSolo(name, PsxTrack1)], (name, PsxTrack1));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(PsxTrack1));
        AssertNotOpen(Path.Combine(folder, name));
    }

    [Test]
    public async Task PlayDisc_AThreeDoCueSet_IsAStoredZip_WithThePlaybackCue()
    {
        var cue = PsxCue("Opal Rally (USA) (Track 1).bin", "Opal Rally (USA) (Track 2).bin");
        var (id, _, ids) = await ThreeDoGame("Opal Rally", [PsxSet("Opal Rally (USA)", PsxTrack1, cue, PsxTrack2)],
            ("Opal Rally (USA) (Track 1).bin", PsxTrack1), ("Opal Rally (USA).cue", cue), ("Opal Rally (USA) (Track 2).bin", PsxTrack2));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Opal Rally (USA).cue", "track-01.bin", "track-02.bin" }));
        Assert.That(zip.Entries.Select(e => e.CompressedLength == e.Length), Has.All.True, "stored, not deflated");
        Assert.That(ReadEntry(zip, "track-01.bin"), Is.EqualTo(PsxTrack1));
        Assert.That(ReadEntry(zip, "track-02.bin"), Is.EqualTo(PsxTrack2));
    }

    [Test]
    public async Task PlayDisc_AThreeDoDiscOfExactlyTwoGiB_Streams_AndOneByteMoreIs409PlayDiscTooLarge()
    {
        var (atId, atFolder, atRoms) = await ThreeDoGame("Opal Wide", [PsxSolo("Opal Wide (USA).iso", Umd(60))], ("Opal Wide (USA).iso", Umd(60)));
        var (overId, overFolder, overRoms) = await ThreeDoGame("Opal Wider", [PsxSolo("Opal Wider (USA).iso", Umd(61))], ("Opal Wider (USA).iso", Umd(61)));
        // Lengths are set on disk only: the stored sizes stay the small originals.
        await using (var grown = new FileStream(Path.Combine(atFolder, "Opal Wide (USA).iso"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes);
        await using (var grown = new FileStream(Path.Combine(overFolder, "Opal Wider (USA).iso"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes + 1);

        using (var atLimit = await _user.SendAsync(HttpMethod.Get, PlayDiscUrl(atId, atRoms[0]), null, sendXsrf: false,
            completion: HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.That(atLimit.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(atLimit.Content.Headers.ContentLength, Is.EqualTo(PlayDisc.MaxBytes));
        } // disposed with the body unread: the stream is cancelled

        using var over = await _user.GetAsync(PlayDiscUrl(overId, overRoms[0]));
        Assert.That(over.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await AuthClient.JsonAsync(over);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo(PlayDisc.PlayDiscTooLarge));
        Assert.That(problem.GetProperty("title").GetString(), Does.Contain("2 GiB"));
        AssertNotOpen(Path.Combine(overFolder, "Opal Wider (USA).iso"));
    }

    [Test]
    public async Task PlayDisc_AThreeDoSetWithTwoCues_Is409InvalidPlayDisc()
    {
        var cue = PsxCue("Opal Twin (USA) (Track 1).bin", "Opal Twin (USA) (Track 2).bin");
        var (id, _, ids) = await ThreeDoGame("Opal Twin",
            [PsxSolo("Opal Twin (USA) (Track 1).bin", PsxTrack1) with
            {
                Files = [SetFile("Opal Twin (USA).cue", cue), SetFile("Opal Twin (USA) (Alt).cue", cue), SetFile("Opal Twin (USA) (Track 2).bin", PsxTrack2)],
            }],
            ("Opal Twin (USA) (Track 1).bin", PsxTrack1), ("Opal Twin (USA).cue", cue), ("Opal Twin (USA) (Alt).cue", cue),
            ("Opal Twin (USA) (Track 2).bin", PsxTrack2));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(await ProblemCode(response, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.InvalidPlayDisc));
    }

    // TurboGrafx-CD B4: cue sets and a standalone .chd only; Beetle PCE would run a bare .bin as a HuCard.

    private Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> TurboGrafxCdGame(string title, InfoJsonRom[] roms,
        params (string Name, byte[] Bytes)[] onDisk) => DiscGame(PlayDisc.TurboGrafxCdSystemId, title, roms, onDisk);

    [Test]
    public async Task PlayDisc_ATurboGrafxCdCueSet_IsAStoredZip_WithThePlaybackCue()
    {
        var cue = PsxCue("Ninja Run (Japan) (Track 01).bin", "Ninja Run (Japan) (Track 02).bin");
        // As Redump lays it out: audio Track 01, then the data Track 02, which the DAT names and is the release's main file.
        var (id, _, ids) = await TurboGrafxCdGame("Ninja Run",
            [PsxSolo("Ninja Run (Japan) (Track 02).bin", PsxTrack2) with
            {
                Files = [SetFile("Ninja Run (Japan).cue", cue), SetFile("Ninja Run (Japan) (Track 01).bin", PsxTrack1)],
            }],
            ("Ninja Run (Japan) (Track 02).bin", PsxTrack2), ("Ninja Run (Japan).cue", cue), ("Ninja Run (Japan) (Track 01).bin", PsxTrack1));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Ninja Run (Japan).cue", "track-01.bin", "track-02.bin" }));
        Assert.That(zip.Entries.Select(e => e.CompressedLength == e.Length), Has.All.True, "stored, not deflated");
        Assert.That(ReadEntry(zip, "track-01.bin"), Is.EqualTo(PsxTrack1));
        Assert.That(ReadEntry(zip, "track-02.bin"), Is.EqualTo(PsxTrack2));
    }

    [TestCase("Ninja Solo (Japan).bin")]
    [TestCase("Ninja Solo (Japan).BIN")]
    [TestCase("Ninja Solo (Japan).iso")]
    [TestCase("Ninja Solo (Japan).img")]
    public async Task PlayDisc_ATurboGrafxCdSingleFileThatIsNotAChd_Is409InvalidPlayDisc(string name)
    {
        var (id, folder, ids) = await TurboGrafxCdGame("Ninja Solo", [PsxSolo(name, PsxTrack1)], (name, PsxTrack1));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await AuthClient.JsonAsync(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo(PlayDisc.InvalidPlayDisc));
        Assert.That(problem.GetProperty("title").GetString(),
            Is.EqualTo("The disc can't be played: a TurboGrafx-CD disc must be a .cue with its .bin tracks, or a .chd."));
        AssertNotOpen(Path.Combine(folder, name));
    }

    // PC-FX X5: TurboGrafx-CD's shapes; Beetle PC-FX opens only cue|ccd|toc|chd.

    private Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> PcfxGame(string title, InfoJsonRom[] roms,
        params (string Name, byte[] Bytes)[] onDisk) => DiscGame(PlayDisc.PcfxSystemId, title, roms, onDisk);

    [Test]
    public async Task PlayDisc_APcfxCueSet_IsAStoredZip_WithThePlaybackCue()
    {
        var cue = PsxCue("Fx Run (Japan) (Track 1).bin", "Fx Run (Japan) (Track 2).bin");
        var (id, _, ids) = await PcfxGame("Fx Run",
            [PsxSolo("Fx Run (Japan) (Track 2).bin", PsxTrack2) with
            {
                Files = [SetFile("Fx Run (Japan).cue", cue), SetFile("Fx Run (Japan) (Track 1).bin", PsxTrack1)],
            }],
            ("Fx Run (Japan) (Track 2).bin", PsxTrack2), ("Fx Run (Japan).cue", cue), ("Fx Run (Japan) (Track 1).bin", PsxTrack1));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Fx Run (Japan).cue", "track-01.bin", "track-02.bin" }));
        Assert.That(zip.Entries.Select(e => e.CompressedLength == e.Length), Has.All.True, "stored, not deflated");
        Assert.That(ReadEntry(zip, "track-01.bin"), Is.EqualTo(PsxTrack1));
        Assert.That(ReadEntry(zip, "track-02.bin"), Is.EqualTo(PsxTrack2));
    }

    [TestCase("Fx Solo (Japan).bin")]
    [TestCase("Fx Solo (Japan).BIN")]
    [TestCase("Fx Solo (Japan).iso")]
    [TestCase("Fx Solo (Japan).img")]
    public async Task PlayDisc_APcfxSingleFileThatIsNotAChd_Is409InvalidPlayDisc(string name)
    {
        var (id, folder, ids) = await PcfxGame("Fx Solo", [PsxSolo(name, PsxTrack1)], (name, PsxTrack1));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await AuthClient.JsonAsync(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo(PlayDisc.InvalidPlayDisc));
        Assert.That(problem.GetProperty("title").GetString(),
            Is.EqualTo("The disc can't be played: a PC-FX disc must be a .cue with its .bin tracks, or a .chd."));
        AssertNotOpen(Path.Combine(folder, name));
    }

    [Test]
    public async Task PlayDisc_ATurboGrafxCdSetWithTwoCues_Is409InvalidPlayDisc()
    {
        var cue = PsxCue("Ninja Twin (Japan) (Track 01).bin", "Ninja Twin (Japan) (Track 02).bin");
        var (id, _, ids) = await TurboGrafxCdGame("Ninja Twin",
            [PsxSolo("Ninja Twin (Japan) (Track 01).bin", PsxTrack1) with
            {
                Files = [SetFile("Ninja Twin (Japan).cue", cue), SetFile("Ninja Twin (Japan) (Alt).cue", cue), SetFile("Ninja Twin (Japan) (Track 02).bin", PsxTrack2)],
            }],
            ("Ninja Twin (Japan) (Track 01).bin", PsxTrack1), ("Ninja Twin (Japan).cue", cue), ("Ninja Twin (Japan) (Alt).cue", cue),
            ("Ninja Twin (Japan) (Track 02).bin", PsxTrack2));

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(await ProblemCode(response, HttpStatusCode.Conflict), Is.EqualTo(PlayDisc.InvalidPlayDisc));
    }

    // CloneCD B8: a generated cue, the aliased .img and .sub; the stored .ccd is never sent.

    private static readonly byte[] CloneSub = [.. Enumerable.Range(0, 96 * 3).Select(i => (byte)(i * 5 + 2))];

    private static byte[] CloneCcd(string sessions = "1") =>
        Encoding.UTF8.GetBytes($"[Disc]\nSessions={sessions}\n[TRACK 1]\nMODE=2\nINDEX 1=0\n");

    /// <summary>A CloneCD release: <c>&lt;stem&gt;.img</c> the main file, its <c>.ccd</c> and (unless null) its <c>.sub</c>.</summary>
    private static InfoJsonRom CloneCdRom(string stem, byte[] img, byte[] ccd, byte[]? sub, bool isPrimary = true) =>
        PsxSolo($"{stem}.img", img, isPrimary) with
        {
            Files = [SetFile($"{stem}.ccd", ccd), .. sub is null ? Array.Empty<InfoJsonRomFile>() : [SetFile($"{stem}.sub", sub)]],
        };

    private Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> CloneCdGame(string system, string title, byte[] img, byte[] ccd, byte[]? sub)
    {
        var stem = $"{title} (USA)";
        (string, byte[])[] onDisk = [($"{stem}.img", img), ($"{stem}.ccd", ccd), .. sub is null ? Array.Empty<(string, byte[])>() : [($"{stem}.sub", sub)]];
        return DiscGame(system, title, [CloneCdRom(stem, img, ccd, sub)], onDisk);
    }

    [Test]
    public async Task PlayDisc_ASaturnCloneCdDisc_IsAStoredZip_WithTheGeneratedCue_TheImgAndTheSub_AndNoCcd()
    {
        var (id, folder, ids) = await CloneCdGame(PlayDisc.SaturnSystemId, "Clone Sky", PsxTrack1, CloneCcd(), CloneSub);

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Clone Sky (USA).cue", "track-01.img", "Clone Sky (USA).sub" }));
        Assert.That(zip.Entries.Select(e => e.CompressedLength == e.Length), Has.All.True, "stored, not deflated");
        Assert.That(Encoding.UTF8.GetString(ReadEntry(zip, "Clone Sky (USA).cue")),
            Is.EqualTo("FILE \"track-01.img\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"));
        Assert.That(ReadEntry(zip, "track-01.img"), Is.EqualTo(PsxTrack1));
        Assert.That(ReadEntry(zip, "Clone Sky (USA).sub"), Is.EqualTo(CloneSub));
        foreach (var name in Directory.GetFiles(folder, "Clone Sky*"))
            AssertNotOpen(name);
    }

    [Test]
    public async Task PlayDisc_ACloneCdDiscWithoutSub_HoldsTheCueAndTheImg()
    {
        var (id, _, ids) = await CloneCdGame(PlayDisc.SystemId, "Clone Bare", PsxTrack1, CloneCcd(), null);

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Clone Bare (USA).cue", "track-01.img" }));
    }

    [TestCase("Bad", "track 1 has no MODE")]
    [TestCase("Multi", "multi-session CloneCD images aren't supported")]
    [TestCase("Partial", "the .img isn't a whole number of 2352-byte sectors")]
    public async Task PlayDisc_ACloneCdDiscThatCantPlay_Is409InvalidPlayDisc(string kind, string error)
    {
        var ccd = kind switch
        {
            "Bad" => Encoding.UTF8.GetBytes("[TRACK 1]\nINDEX 1=0\n"),
            "Multi" => CloneCcd("2"),
            _ => CloneCcd(),
        };
        var img = kind == "Partial" ? PsxTrack1[..^1] : PsxTrack1;
        var (id, folder, ids) = await CloneCdGame(PlayDisc.SegaCdSystemId, "Clone " + kind, img, ccd, CloneSub);

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(await ProblemTitle(response, HttpStatusCode.Conflict, PlayDisc.InvalidPlayDisc), Is.EqualTo($"The disc can't be played: {error}."));
        foreach (var name in Directory.GetFiles(folder, "Clone*"))
            AssertNotOpen(name);
    }

    [Test]
    public async Task PlayDisc_ATurboGrafxCdCloneCdDisc_Plays()
    {
        var (id, _, ids) = await CloneCdGame(PlayDisc.TurboGrafxCdSystemId, "Clone Ninja", PsxTrack1, CloneCcd(), CloneSub);

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Clone Ninja (USA).cue", "track-01.img", "Clone Ninja (USA).sub" }));
    }

    [Test]
    public async Task PlayDisc_APcfxCloneCdDisc_Plays()
    {
        var (id, _, ids) = await CloneCdGame(PlayDisc.PcfxSystemId, "Clone Fox", PsxTrack1, CloneCcd(), CloneSub);

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Clone Fox (USA).cue", "track-01.img", "Clone Fox (USA).sub" }));
    }

    // File type gaps D11–D12: a cue generated from the .toc or .mds and the aliased tracks; the stored sheet is never sent.

    private static readonly byte[] MdsOneTrack = SyntheticMdsBuilder.OneTrack();

    /// <summary>An MDS release: <c>&lt;stem&gt;.mdf</c> the main file and <c>&lt;mdsStem&gt;.mds</c> beside it.</summary>
    private Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> MdsGame(string system, string title, byte[] mdf, byte[] mds,
        string? mdsStem = null)
    {
        var stem = $"{title} (USA)";
        var mdsName = $"{mdsStem ?? stem}.mds";
        return DiscGame(system, title, [PsxSolo($"{stem}.mdf", mdf) with { Files = [SetFile(mdsName, mds)] }], ($"{stem}.mdf", mdf), (mdsName, mds));
    }

    private static byte[] TocOf(string track1, string track2, string mode = "MODE2_RAW") => Encoding.UTF8.GetBytes(
        $"CD_ROM_XA\nTRACK {mode}\nDATAFILE \"{track1}\" 00:00:03\nTRACK AUDIO\nFILE \"{track2}\" 0\nSTART 00:00:01\n");

    /// <summary>A TOC release: <c>&lt;stem&gt; (Track 1).bin</c> the main file, its <c>.toc</c> and <c>(Track 2).bin</c>.</summary>
    private Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> TocGame(string system, string title, byte[] toc)
    {
        var stem = $"{title} (USA)";
        var (one, two) = ($"{stem} (Track 1).bin", $"{stem} (Track 2).bin");
        return DiscGame(system, title, [PsxSolo(one, PsxTrack1) with { Files = [SetFile($"{stem}.toc", toc), SetFile(two, PsxTrack2)] }],
            (one, PsxTrack1), ($"{stem}.toc", toc), (two, PsxTrack2));
    }

    private async Task<ZipArchive> PlayZipAsync(Guid id, long romId)
    {
        using var response = await _user.GetAsync(PlayDiscUrl(id, romId));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        return new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
    }

    [Test]
    public async Task PlayDisc_ASaturnMdsDisc_IsTheGeneratedCueAndTheMdf_AndNoMds()
    {
        var (id, folder, ids) = await MdsGame(PlayDisc.SaturnSystemId, "Mds Sky", PsxTrack1, MdsOneTrack);

        using var zip = await PlayZipAsync(id, ids[0]);

        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Mds Sky (USA).cue", "track-01.bin" }));
        Assert.That(Encoding.UTF8.GetString(ReadEntry(zip, "Mds Sky (USA).cue")),
            Is.EqualTo("FILE \"track-01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"));
        Assert.That(ReadEntry(zip, "track-01.bin"), Is.EqualTo(PsxTrack1));
        foreach (var name in Directory.GetFiles(folder, "Mds Sky*"))
            AssertNotOpen(name);
    }

    [TestCase("psx")]
    [TestCase("tgcd")]
    [TestCase("pcfx")]
    public async Task PlayDisc_ATocSet_IsTheGeneratedCueAndTheAliasedBins_AndNoToc(string system)
    {
        var (id, _, ids) = await TocGame(system, "Toc Star", TocOf("Toc Star (USA) (Track 1).bin", "Toc Star (USA) (Track 2).bin"));

        using var zip = await PlayZipAsync(id, ids[0]);

        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Toc Star (USA).cue", "track-01.bin", "track-02.bin" }));
        Assert.That(Encoding.UTF8.GetString(ReadEntry(zip, "Toc Star (USA).cue")), Is.EqualTo(
            "FILE \"track-01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"
            + "FILE \"track-02.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 00 00:00:00\n    INDEX 01 00:00:01\n"));
        Assert.That(ReadEntry(zip, "track-02.bin"), Is.EqualTo(PsxTrack2));
    }

    [TestCase("SubMdf", "the .mdf stores subchannel data, which can't be played")]
    [TestCase("Cooked", "track 1: MODE1 can't be played; only AUDIO, MODE1_RAW and MODE2_RAW can")]
    [TestCase("Stems", "a disc set must be one .cue or .toc and its .bin tracks, the main file among them")]
    [TestCase("BigToc", "the .toc is larger than 65536 bytes")]
    [TestCase("BigMds", "the .mds is larger than 65536 bytes")]
    public async Task PlayDisc_AnMdsOrTocDiscThatCantPlay_Is409InvalidPlayDisc(string kind, string error)
    {
        var big = new byte[IngestSetPlanner.MaxCueBytes + 1];
        var (id, folder, ids) = kind switch
        {
            "SubMdf" => await MdsGame(PlayDisc.SystemId, "Bad " + kind, PsxTrack1,
                SyntheticMdsBuilder.Build([new MdsBlock(1, Sub: 8, SectorSize: 2448)])),
            "Stems" => await MdsGame(PlayDisc.SegaCdSystemId, "Bad " + kind, PsxTrack1, MdsOneTrack, mdsStem: "Other (USA)"),
            "BigMds" => await MdsGame(PlayDisc.ThreeDoSystemId, "Bad " + kind, PsxTrack1, big),
            "BigToc" => await TocGame(PlayDisc.SystemId, "Bad " + kind, big),
            _ => await TocGame(PlayDisc.SystemId, "Bad " + kind, TocOf("Bad Cooked (USA) (Track 1).bin", "Bad Cooked (USA) (Track 2).bin", "MODE1")),
        };

        using var response = await _user.GetAsync(PlayDiscUrl(id, ids[0]));

        Assert.That(await ProblemTitle(response, HttpStatusCode.Conflict, PlayDisc.InvalidPlayDisc), Is.EqualTo($"The disc can't be played: {error}."));
        foreach (var name in Directory.GetFiles(folder, "Bad*"))
            AssertNotOpen(name);
    }
}
