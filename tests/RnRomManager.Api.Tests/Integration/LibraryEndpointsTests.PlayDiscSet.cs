using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Library;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Multi-disc M3–M4 (Amiga F3, F5): a game's disc or disk sets in the detail, and <c>/play-disc-set</c>.</summary>
public partial class LibraryEndpointsTests
{
    /// <summary>Disc <paramref name="n"/> of <c>Odyssey (USA)</c> as a standalone image (M1's identifying fields shared).</summary>
    private static InfoJsonRom OdysseyDisc(int n, byte[] bytes, string ext = ".chd", string title = "Odyssey") =>
        PsxSolo($"{title} (USA) (Disc {n}){ext}", bytes, isPrimary: n == 1) with { BaseTitle = title, Regions = ["USA"], Flags = [$"Disc {n}"] };

    /// <summary>Disc <paramref name="n"/> of <c>Odyssey (USA)</c> as a two-track cue set.</summary>
    private static InfoJsonRom OdysseyCueDisc(int n, byte[] track1, byte[] track2, string title = "Odyssey")
    {
        var disc = $"{title} (USA) (Disc {n})";
        return PsxSet(disc, track1, OdysseyCue(disc), track2, isPrimary: n == 1)
            with { BaseTitle = title, Regions = ["USA"], Flags = [$"Disc {n}"] };
    }

    private static byte[] OdysseyCue(string disc) => PsxCue($"{disc} (Track 1).bin", $"{disc} (Track 2).bin");

    private static JsonElement DiscSetOf(JsonElement detail, string fileName) => Release(detail, fileName).GetProperty("discSet");

    [Test]
    public async Task Detail_DiscSet_IsOnDiscOne_WithTheNameAndOrderedIds_AndNullElsewhere()
    {
        var (id, _, ids) = await PsxGame("Odyssey",
            [OdysseyDisc(2, Umd(41)), OdysseyCueDisc(1, PsxTrack1, PsxTrack2), OdysseyDisc(3, Umd(42)), PsxSolo("Bonus (USA).chd", Umd(43), isPrimary: false)]);

        var detail = await GetJson($"/api/games/{id}");

        var set = DiscSetOf(detail, "Odyssey (USA) (Disc 1) (Track 1).bin");
        Assert.That(set.GetProperty("name").GetString(), Is.EqualTo("Odyssey (USA).m3u"));
        Assert.That(set.GetProperty("romIds").EnumerateArray().Select(e => e.GetInt64()), Is.EqualTo(new[] { ids[1], ids[0], ids[2] }));
        foreach (var other in new[] { "Odyssey (USA) (Disc 2).chd", "Odyssey (USA) (Disc 3).chd", "Bonus (USA).chd" })
            Assert.That(DiscSetOf(detail, other).ValueKind, Is.EqualTo(JsonValueKind.Null), other);
    }

    [Test]
    public async Task Detail_DiscSet_IsNullForALoneDisc_AndOnAnotherSystem()
    {
        var (lone, _, _) = await PsxGame("Lonely", [OdysseyDisc(1, Umd(44))]);
        var (threeDo, _, _) = await DiscGame(PlayDisc.ThreeDoSystemId, "Odyssey 3DO", [OdysseyDisc(1, Umd(45)), OdysseyDisc(2, Umd(46))]);

        Assert.That(DiscSetOf(await GetJson($"/api/games/{lone}"), "Odyssey (USA) (Disc 1).chd").ValueKind, Is.EqualTo(JsonValueKind.Null));
        var threeDoDetail = await GetJson($"/api/games/{threeDo}");
        Assert.That(DiscSetOf(threeDoDetail, "Odyssey (USA) (Disc 1).chd").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(DiscSetOf(threeDoDetail, "Odyssey (USA) (Disc 2).chd").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [TestCase("saturn", "Ark")]
    [TestCase("segacd", "Bay")]
    public async Task Detail_DiscSet_IsOnDiscOne_ForSaturnAndSegaCd(string system, string title)
    {
        var (id, _, ids) = await DiscGame(system, title,
            [OdysseyDisc(1, Umd(70), ".bin", title), OdysseyDisc(2, Umd(71), title: title)]);

        var set = DiscSetOf(await GetJson($"/api/games/{id}"), $"{title} (USA) (Disc 1).bin");
        Assert.That(set.GetProperty("name").GetString(), Is.EqualTo($"{title} (USA).m3u"));
        Assert.That(set.GetProperty("romIds").EnumerateArray().Select(e => e.GetInt64()), Is.EqualTo(ids));
    }

    // Amiga F3: floppy disk sets of releases stored inside the game's ZIP.

    private static string TwinDisk(int n, int m = 2, string tags = "") => $"Twin Disk (1990)(Synth)(Disk {n} of {m}){tags}.adf";

    /// <summary>
    /// Mirrors an Amiga game at <c>amiga/&lt;letter&gt;/&lt;title&gt;.zip</c> with one embedded release per name, each holding
    /// <c>TestLibrary.RomBytes(seed)</c>, as <c>LibraryEditorSaveTests</c> writes its games.
    /// </summary>
    private Task<(Guid Id, string Zip, IReadOnlyList<long> RomIds, IReadOnlyList<byte[]> Bytes)> AmigaGame(string title,
        int seed, params string[] fileNames) => AmigaGame(title, seed, null, fileNames);

    /// <param name="shape">Changes release <c>i</c>'s row in the mirrored database only (the ZIP is written as given).</param>
    private async Task<(Guid Id, string Zip, IReadOnlyList<long> RomIds, IReadOnlyList<byte[]> Bytes)> AmigaGame(string title,
        int seed, Func<InfoJsonRom, int, InfoJsonRom>? shape, params string[] fileNames)
    {
        var seeds = fileNames.Select((_, i) => seed + i).ToArray();
        var game = TestLibrary.Game(title, [.. fileNames.Select((f, i) => TestLibrary.Rom(f, TestLibrary.RomBytes(seeds[i]), isPrimary: i == 0))])
            with { System = "amiga" };
        var zipPath = $"amiga/{title[..1]}/{title}.zip";
        var zip = Path.Combine(_factory.Root, "roms", zipPath);
        await TestLibrary.WriteZipAsync(Service<InfoJsonReader>(), zip, game, seeds);
        var file = new FileInfo(zip);
        var mirrored = shape is null ? game : game with { Roms = [.. game.Roms.Select((r, i) => shape(r, i))] };
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(mirrored, zipPath, file.Length, file.LastWriteTimeUtc), default);
        var detail = await GetJson($"/api/games/{game.GameId}");
        return (game.GameId, zip, [.. fileNames.Select(f => Release(detail, f).GetProperty("id").GetInt64())],
            [.. seeds.Select(TestLibrary.RomBytes)]);
    }

    [Test]
    public async Task Detail_DiscSet_IsOnAmigaDiskOne_WithTheNameAndOrderedIds_AndNullElsewhere()
    {
        var (id, _, ids, _) = await AmigaGame("Twin Disk", 9101, TwinDisk(2), TwinDisk(1, tags: "[cr CSL]"));

        var detail = await GetJson($"/api/games/{id}");

        var set = DiscSetOf(detail, TwinDisk(1, tags: "[cr CSL]"));
        Assert.That(set.GetProperty("name").GetString(), Is.EqualTo("Twin Disk (1990)(Synth)[cr CSL].m3u"));
        Assert.That(set.GetProperty("romIds").EnumerateArray().Select(e => e.GetInt64()), Is.EqualTo(new[] { ids[1], ids[0] }));
        Assert.That(DiscSetOf(detail, TwinDisk(2)).ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task Detail_DiscSet_IsNullForAnAmigaGameWithTwoCandidatesForOneDisk()
    {
        var (id, _, _, _) = await AmigaGame("Tangle Disk", 9111,
            "Tangle Disk (1990)(Synth)(Disk 1 of 2)[cr CSL].adf", "Tangle Disk (1990)(Synth)(Disk 1 of 2)[cr QTX].adf",
            "Tangle Disk (1990)(Synth)(Disk 2 of 2).adf");

        var detail = await GetJson($"/api/games/{id}");

        foreach (var release in detail.GetProperty("roms").EnumerateArray())
            Assert.That(release.GetProperty("discSet").ValueKind, Is.EqualTo(JsonValueKind.Null), release.GetProperty("fileName").GetString());
    }

    // Amiga F5: play-disc-set for a floppy set inside the game's ZIP.

    private static string Twin(string title, int n) => TwinDisk(n).Replace("Twin Disk", title);

    [Test]
    public async Task PlayDiscSet_AnAmigaSet_IsThePlaylist_ThenEachDiskAliased()
    {
        var (id, _, ids, bytes) = await AmigaGame("Twin Stream", 9201, Twin("Twin Stream", 1), Twin("Twin Stream", 2));

        using var response = await _user.SendAsync(HttpMethod.Get, PlayDiscSetUrl(id, ids[0]), null, sendXsrf: false,
            completion: HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(response.Content.Headers.ContentLength, Is.Null);
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Twin Stream (1990)(Synth).m3u", "Disk 1.adf", "Disk 2.adf" }));
        Assert.That(zip.Entries.Select(e => e.CompressedLength == e.Length), Has.All.True, "stored, not deflated");
        Assert.That(ReadEntry(zip, "Twin Stream (1990)(Synth).m3u"), Is.EqualTo(Encoding.UTF8.GetBytes("Disk 1.adf\nDisk 2.adf\n")));
        Assert.That(ReadEntry(zip, "Disk 1.adf"), Is.EqualTo(bytes[0]));
        Assert.That(ReadEntry(zip, "Disk 2.adf"), Is.EqualTo(bytes[1]));
        var payload = zip.Entries.Sum(e => e.Length);
        Assert.That(response.Headers.GetValues("X-Content-Bytes"), Is.EqualTo(new[] { payload.ToString(CultureInfo.InvariantCulture) }));
    }

    [Test]
    public async Task PlayDiscSet_AmigaDiskTwo_AnAmbiguousGame_OrAnExternalDisk_Is404()
    {
        var (set, _, setIds, _) = await AmigaGame("Twin Gate", 9211, Twin("Twin Gate", 1), Twin("Twin Gate", 2));
        var (tangle, _, tangleIds, _) = await AmigaGame("Twin Knot", 9221, "Twin Knot (1990)(Synth)(Disk 1 of 2)[a].adf",
            "Twin Knot (1990)(Synth)(Disk 1 of 2)[b].adf", "Twin Knot (1990)(Synth)(Disk 2 of 2).adf");
        var (external, _, externalIds, _) = await AmigaGame("Twin Away", 9231,
            (rom, i) => i == 1 ? rom with { ExternalFileName = rom.FileName } : rom, Twin("Twin Away", 1), Twin("Twin Away", 2));

        foreach (var url in new[] { PlayDiscSetUrl(set, setIds[1]), PlayDiscSetUrl(tangle, tangleIds[0]), PlayDiscSetUrl(external, externalIds[0]) })
        {
            using var response = await _user.GetAsync(url);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), url);
        }
    }

    /// <summary>Rewrites (or, with null bytes, deletes) a disk's ZIP entry after the ZIP is written.</summary>
    private static void ReplaceDisk(string zip, string fileName, byte[]? bytes)
    {
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
        archive.GetEntry(LibraryZip.RomEntry(fileName))!.Delete();
        if (bytes is null)
            return;
        using var target = archive.CreateEntry(LibraryZip.RomEntry(fileName)).Open();
        target.Write(bytes);
    }

    [Test]
    public async Task PlayDiscSet_AnEmptyAmigaDisk_Is409InvalidPlayDisc()
    {
        var (id, zip, ids, _) = await AmigaGame("Twin Hollow", 9241, Twin("Twin Hollow", 1), Twin("Twin Hollow", 2));
        ReplaceDisk(zip, Twin("Twin Hollow", 2), []);

        using var response = await _user.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(await ProblemTitle(response, HttpStatusCode.Conflict, PlayDisc.InvalidPlayDisc),
            Is.EqualTo($"The disc can't be played: Disk 2: '{Twin("Twin Hollow", 2)}' is empty."));
    }

    [Test]
    public async Task PlayDiscSet_AMissingAmigaDisk_Is404_AndAnOversizedOne_Is500IoError()
    {
        var (goneId, goneZip, goneIds, _) = await AmigaGame("Twin Gone", 9251, Twin("Twin Gone", 1), Twin("Twin Gone", 2));
        ReplaceDisk(goneZip, Twin("Twin Gone", 2), null);
        var (bigId, bigZip, bigIds, bigBytes) = await AmigaGame("Twin Grown", 9261, Twin("Twin Grown", 1), Twin("Twin Grown", 2));
        ReplaceDisk(bigZip, Twin("Twin Grown", 2), [.. bigBytes[1], 0]);

        using var gone = await _user.GetAsync(PlayDiscSetUrl(goneId, goneIds[0]));
        using var grown = await _user.GetAsync(PlayDiscSetUrl(bigId, bigIds[0]));

        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(grown.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(await AuthClient.CodeAsync(grown), Is.EqualTo("IoError"));
    }

    [Test]
    public async Task PlayDiscSet_AnAmigaSet_AnonymousIs401_AndAForcedPasswordChangeIs403()
    {
        var (id, _, ids, _) = await AmigaGame("Twin Lock", 9271, Twin("Twin Lock", 1), Twin("Twin Lock", 2));
        using var anonymous = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "forced-floppy", mustChangePassword: true);
        using var forced = await AuthClient.CreateAsync(_factory);
        (await forced.LoginAsync("forced-floppy", TestUsers.Password)).Dispose();

        using var asAnonymous = await anonymous.GetAsync(PlayDiscSetUrl(id, ids[0]));
        using var asForced = await forced.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(asAnonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(asForced.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await AuthClient.CodeAsync(asForced), Is.EqualTo("PasswordChangeRequired"));
    }

    private static string PlayDiscSetUrl(Guid id, long romId) => $"/api/games/{id}/roms/{romId}/play-disc-set";

    /// <summary>A three-disc set of <paramref name="title"/> on disk: Disc 1 a cue set, Disc 2 a <c>.chd</c>, Disc 3 an <c>.iso</c>.</summary>
    private async Task<(Guid Id, string Folder, IReadOnlyList<long> RomIds)> ThreeDiscSet(string title, int seed)
    {
        var one = $"{title} (USA) (Disc 1)";
        return await PsxGame(title,
            [OdysseyCueDisc(1, SetTrack1(seed), PsxTrack2, title), OdysseyDisc(2, Umd(seed), title: title), OdysseyDisc(3, Umd(seed + 1), ".iso", title)],
            ($"{one} (Track 1).bin", SetTrack1(seed)), ($"{one}.cue", OdysseyCue(one)), ($"{one} (Track 2).bin", PsxTrack2),
            ($"{title} (USA) (Disc 2).chd", Umd(seed)), ($"{title} (USA) (Disc 3).iso", Umd(seed + 1)));
    }

    private static async Task<string> ProblemTitle(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.That(response.StatusCode, Is.EqualTo(status));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True, "endpoint errors are not cached");
        var problem = await AuthClient.JsonAsync(response);
        Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo(code));
        return problem.GetProperty("title").GetString()!;
    }

    /// <summary>A distinct Track 1 per seed, so two sets in one test never share a main file's sha1.</summary>
    private static byte[] SetTrack1(int seed) => [.. PsxTrack1.Select((b, i) => i == 0 ? (byte)seed : b)];

    [Test]
    public async Task PlayDiscSet_IsAnUncompressedZip_ThePlaylistFirst_ThenEachDisc()
    {
        var (id, folder, ids) = await ThreeDiscSet("Voyage", 50);

        using var response = await _user.SendAsync(HttpMethod.Get, PlayDiscSetUrl(id, ids[0]), null, sendXsrf: false,
            completion: HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(response.Content.Headers.ContentLength, Is.Null);
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[]
        {
            "Voyage (USA).m3u", "Voyage (USA) (Disc 1).cue", "disc1-track-01.bin", "disc1-track-02.bin",
            "Voyage (USA) (Disc 2).chd", "Voyage (USA) (Disc 3).iso",
        }));
        Assert.That(zip.Entries.Select(e => e.CompressedLength == e.Length), Has.All.True, "stored, not deflated");
        Assert.That(ReadEntry(zip, "Voyage (USA).m3u"),
            Is.EqualTo(Encoding.UTF8.GetBytes("Voyage (USA) (Disc 1).cue\nVoyage (USA) (Disc 2).chd\nVoyage (USA) (Disc 3).iso\n")));
        Assert.That(Encoding.UTF8.GetString(ReadEntry(zip, "Voyage (USA) (Disc 1).cue")), Does.StartWith("FILE \"disc1-track-01.bin\" BINARY\n"));
        Assert.That(ReadEntry(zip, "disc1-track-01.bin"), Is.EqualTo(SetTrack1(50)));
        Assert.That(ReadEntry(zip, "disc1-track-02.bin"), Is.EqualTo(PsxTrack2));
        Assert.That(ReadEntry(zip, "Voyage (USA) (Disc 2).chd"), Is.EqualTo(Umd(50)));
        Assert.That(ReadEntry(zip, "Voyage (USA) (Disc 3).iso"), Is.EqualTo(Umd(51)));
        var payload = zip.Entries.Sum(e => e.Length);
        Assert.That(response.Headers.GetValues("X-Content-Bytes"), Is.EqualTo(new[] { payload.ToString(CultureInfo.InvariantCulture) }));
        foreach (var name in Directory.GetFiles(folder, "Voyage*"))
            AssertNotOpen(name);
    }

    [Test]
    public async Task PlayDiscSet_ATwoDiscPsxCloneCdSet_NamesEachGeneratedCue_AndAliasesEachImg()
    {
        static InfoJsonRom Disc(int n, byte[] img) =>
            CloneCdRom($"Mirror (USA) (Disc {n})", img, CloneCcd(), CloneSub, isPrimary: n == 1) with
            {
                BaseTitle = "Mirror", Regions = ["USA"], Flags = [$"Disc {n}"],
            };
        var (id, _, ids) = await PsxGame("Mirror", [Disc(1, SetTrack1(70)), Disc(2, SetTrack1(71))],
            ("Mirror (USA) (Disc 1).img", SetTrack1(70)), ("Mirror (USA) (Disc 1).ccd", CloneCcd()), ("Mirror (USA) (Disc 1).sub", CloneSub),
            ("Mirror (USA) (Disc 2).img", SetTrack1(71)), ("Mirror (USA) (Disc 2).ccd", CloneCcd()), ("Mirror (USA) (Disc 2).sub", CloneSub));

        using var response = await _user.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[]
        {
            "Mirror (USA).m3u", "Mirror (USA) (Disc 1).cue", "disc1-track-01.img", "Mirror (USA) (Disc 1).sub",
            "Mirror (USA) (Disc 2).cue", "disc2-track-01.img", "Mirror (USA) (Disc 2).sub",
        }));
        Assert.That(Encoding.UTF8.GetString(ReadEntry(zip, "Mirror (USA).m3u")), Is.EqualTo("Mirror (USA) (Disc 1).cue\nMirror (USA) (Disc 2).cue\n"));
        Assert.That(Encoding.UTF8.GetString(ReadEntry(zip, "Mirror (USA) (Disc 2).cue")), Does.StartWith("FILE \"disc2-track-01.img\" BINARY\n"));
        Assert.That(ReadEntry(zip, "disc2-track-01.img"), Is.EqualTo(SetTrack1(71)));
    }

    [Test]
    public async Task PlayDiscSet_NotDiscOne_ALoneDisc_AnotherSystem_OrAMissingFile_Is404()
    {
        var (id, folder, ids) = await ThreeDiscSet("Atlas", 52);
        var (lone, _, loneIds) = await PsxGame("Single", [OdysseyDisc(1, Umd(54), title: "Single")], ("Single (USA) (Disc 1).chd", Umd(54)));
        var (threeDo, _, threeDoIds) = await DiscGame(PlayDisc.ThreeDoSystemId, "Ringed",
            [OdysseyDisc(1, Umd(55), title: "Ringed"), OdysseyDisc(2, Umd(56), title: "Ringed")],
            ("Ringed (USA) (Disc 1).chd", Umd(55)), ("Ringed (USA) (Disc 2).chd", Umd(56)));
        var (gone, goneFolder, goneIds) = await ThreeDiscSet("Gone Set", 57);
        File.Delete(Path.Combine(goneFolder, "Gone Set (USA) (Disc 3).iso"));

        foreach (var url in new[]
        {
            PlayDiscSetUrl(id, ids[1]), PlayDiscSetUrl(lone, loneIds[0]), PlayDiscSetUrl(threeDo, threeDoIds[0]),
            PlayDiscSetUrl(gone, goneIds[0]), PlayDiscSetUrl(Guid.NewGuid(), ids[0]),
        })
        {
            using var response = await _user.GetAsync(url);
            Assert.That(await ProblemCode(response, HttpStatusCode.NotFound), Is.EqualTo("NotFound"), url);
        }
        foreach (var name in Directory.GetFiles(folder, "Atlas*").Concat(Directory.GetFiles(goneFolder, "Gone Set*")))
            AssertNotOpen(name);
    }

    [Test]
    public async Task PlayDiscSet_AnEmptyDisc_IsInvalid_NamingTheDisc()
    {
        var (id, folder, ids) = await ThreeDiscSet("Hollow Set", 58);
        await File.WriteAllBytesAsync(Path.Combine(folder, "Hollow Set (USA) (Disc 2).chd"), []);

        using var response = await _user.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(await ProblemTitle(response, HttpStatusCode.Conflict, PlayDisc.InvalidPlayDisc),
            Is.EqualTo("The disc can't be played: Disc 2: 'Hollow Set (USA) (Disc 2).chd' is empty."));
        foreach (var name in Directory.GetFiles(folder, "Hollow Set*"))
            AssertNotOpen(name);
    }

    [Test]
    public async Task PlayDiscSet_AStandaloneDiscNamedLikeTheFirmware_IsInvalid()
    {
        var bios = OdysseyDisc(1, Umd(60), ".bin", "Firmware") with { FileName = "SCPH5501.BIN", OriginalFileName = "SCPH5501.BIN", ExternalFileName = "SCPH5501.BIN" };
        var (id, _, ids) = await PsxGame("Firmware", [bios, OdysseyDisc(2, Umd(61), title: "Firmware")],
            ("SCPH5501.BIN", Umd(60)), ("Firmware (USA) (Disc 2).chd", Umd(61)));

        using var response = await _user.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(await ProblemTitle(response, HttpStatusCode.Conflict, PlayDisc.InvalidPlayDisc),
            Is.EqualTo("The disc can't be played: Disc 1: a disc file can't be named scph5501.bin."));
    }

    [Test]
    public async Task PlayDiscSet_EntriesWhoseNamesClashIgnoringCase_AreInvalid()
    {
        // Disc 1's stored name equals Disc 2's first track alias (ignoring case).
        var first = OdysseyDisc(1, Umd(62), ".bin", "Clash") with { FileName = "DISC2-TRACK-01.bin", OriginalFileName = "DISC2-TRACK-01.bin", ExternalFileName = "DISC2-TRACK-01.bin" };
        var two = "Clash (USA) (Disc 2)";
        var (id, _, ids) = await PsxGame("Clash", [first, OdysseyCueDisc(2, PsxTrack1, PsxTrack2, "Clash")],
            ("DISC2-TRACK-01.bin", Umd(62)), ($"{two} (Track 1).bin", PsxTrack1), ($"{two}.cue", OdysseyCue(two)), ($"{two} (Track 2).bin", PsxTrack2));

        using var response = await _user.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(await ProblemTitle(response, HttpStatusCode.Conflict, PlayDisc.InvalidPlayDisc),
            Is.EqualTo("The disc can't be played: two entries of the disc set have the same name ignoring case."));
    }

    [Test]
    public async Task PlayDiscSet_OfExactlyTwoGiB_Streams_AndOneByteMoreIs409PlayDiscTooLarge()
    {
        var (atId, atFolder, atIds) = await ThreeDiscSet("Wide Set", 63);
        var (overId, overFolder, overIds) = await ThreeDiscSet("Wider Set", 65);
        var rest = PsxTrack1.Length + PsxTrack2.Length + OdysseyCue("Wide Set (USA) (Disc 1)").Length + Umd(63).Length;
        // Disc 3 grows on disk so the set's opened lengths total the cap, then one byte more.
        await using (var grown = new FileStream(Path.Combine(atFolder, "Wide Set (USA) (Disc 3).iso"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes - rest);
        var overRest = PsxTrack1.Length + PsxTrack2.Length + OdysseyCue("Wider Set (USA) (Disc 1)").Length + Umd(65).Length;
        await using (var grown = new FileStream(Path.Combine(overFolder, "Wider Set (USA) (Disc 3).iso"), FileMode.Open, FileAccess.Write))
            grown.SetLength(PlayDisc.MaxBytes - overRest + 1);

        using (var atLimit = await _user.SendAsync(HttpMethod.Get, PlayDiscSetUrl(atId, atIds[0]), null, sendXsrf: false,
            completion: HttpCompletionOption.ResponseHeadersRead))
            Assert.That(atLimit.StatusCode, Is.EqualTo(HttpStatusCode.OK)); // disposed with the body unread

        using var over = await _user.GetAsync(PlayDiscSetUrl(overId, overIds[0]));
        Assert.That(await ProblemTitle(over, HttpStatusCode.Conflict, PlayDisc.PlayDiscTooLarge), Is.EqualTo("The disc set is larger than 2 GiB."));
        foreach (var name in Directory.GetFiles(overFolder, "Wider Set*"))
            AssertNotOpen(name);
    }

    [Test]
    public async Task PlayDiscSet_AnonymousIs401_AndAForcedPasswordChangeIs403()
    {
        var (id, _, ids) = await ThreeDiscSet("Locked Set", 67);
        using var anonymous = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "forced-set", mustChangePassword: true);
        using var forced = await AuthClient.CreateAsync(_factory);
        (await forced.LoginAsync("forced-set", TestUsers.Password)).Dispose();

        using var asAnonymous = await anonymous.GetAsync(PlayDiscSetUrl(id, ids[0]));
        using var asForced = await forced.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(asAnonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(asForced.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await AuthClient.CodeAsync(asForced), Is.EqualTo("PasswordChangeRequired"));
    }

    // Saturn and Sega CD S3: the same archive, plus a generated cue for a standalone .bin disc.

    private static byte[] LoneBinCue(string bin) =>
        Encoding.UTF8.GetBytes($"FILE \"{bin}\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");

    [Test]
    public async Task PlayDiscSet_ASegaCdSet_GivesALoneBinDiscAGeneratedCue()
    {
        const string title = "Cove";
        var one = $"{title} (USA) (Disc 1)";
        var (id, _, ids) = await DiscGame(PlayDisc.SegaCdSystemId, title,
            [OdysseyCueDisc(1, SetTrack1(72), PsxTrack2, title), OdysseyDisc(2, Umd(73), ".bin", title), OdysseyDisc(3, Umd(74), title: title)],
            ($"{one} (Track 1).bin", SetTrack1(72)), ($"{one}.cue", OdysseyCue(one)), ($"{one} (Track 2).bin", PsxTrack2),
            ($"{title} (USA) (Disc 2).bin", Umd(73)), ($"{title} (USA) (Disc 3).chd", Umd(74)));

        using var response = await _user.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[]
        {
            "Cove (USA).m3u", "Cove (USA) (Disc 1).cue", "disc1-track-01.bin", "disc1-track-02.bin",
            "Cove (USA) (Disc 2).cue", "Cove (USA) (Disc 2).bin", "Cove (USA) (Disc 3).chd",
        }));
        Assert.That(ReadEntry(zip, "Cove (USA).m3u"),
            Is.EqualTo(Encoding.UTF8.GetBytes("Cove (USA) (Disc 1).cue\nCove (USA) (Disc 2).cue\nCove (USA) (Disc 3).chd\n")),
            "one line per disc, no blank line");
        Assert.That(ReadEntry(zip, "Cove (USA) (Disc 2).cue"), Is.EqualTo(LoneBinCue("Cove (USA) (Disc 2).bin")));
        Assert.That(ReadEntry(zip, "Cove (USA) (Disc 2).bin"), Is.EqualTo(Umd(73)));
        var payload = zip.Entries.Sum(e => e.Length);
        Assert.That(response.Headers.GetValues("X-Content-Bytes"), Is.EqualTo(new[] { payload.ToString(CultureInfo.InvariantCulture) }),
            "the generated cue counts");
    }

    [Test]
    public async Task PlayDiscSet_ASaturnSet_Streams_WithACueForItsLoneBin()
    {
        const string title = "Dune Sea";
        var (id, _, ids) = await DiscGame(PlayDisc.SaturnSystemId, title,
            [OdysseyDisc(1, Umd(75), ".bin", title), OdysseyDisc(2, Umd(76), ".iso", title)],
            ($"{title} (USA) (Disc 1).bin", Umd(75)), ($"{title} (USA) (Disc 2).iso", Umd(76)));

        using var response = await _user.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[]
        {
            "Dune Sea (USA).m3u", "Dune Sea (USA) (Disc 1).cue", "Dune Sea (USA) (Disc 1).bin", "Dune Sea (USA) (Disc 2).iso",
        }));
        Assert.That(ReadEntry(zip, "Dune Sea (USA).m3u"),
            Is.EqualTo(Encoding.UTF8.GetBytes("Dune Sea (USA) (Disc 1).cue\nDune Sea (USA) (Disc 2).iso\n")));
        Assert.That(ReadEntry(zip, "Dune Sea (USA) (Disc 1).cue"), Is.EqualTo(LoneBinCue("Dune Sea (USA) (Disc 1).bin")));
    }

    [TestCase("saturn", "SEGA_101.BIN", "sega_101.bin")]
    [TestCase("saturn", "Saturn_Bios.bin", "saturn_bios.bin")]
    [TestCase("segacd", "bios_cd_e.bin", "bios_CD_E.bin")]
    public async Task PlayDiscSet_AStandaloneDiscNamedLikeTheSystemsFirmware_IsInvalid(string system, string stored, string listed)
    {
        var title = "Fw " + system;
        var first = OdysseyDisc(1, Umd(77), ".bin", title) with { FileName = stored, OriginalFileName = stored, ExternalFileName = stored };
        var (id, _, ids) = await DiscGame(system, title, [first, OdysseyDisc(2, Umd(78), title: title)],
            (stored, Umd(77)), ($"{title} (USA) (Disc 2).chd", Umd(78)));

        using var response = await _user.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(await ProblemTitle(response, HttpStatusCode.Conflict, PlayDisc.InvalidPlayDisc),
            Is.EqualTo($"The disc can't be played: Disc 1: a disc file can't be named {listed}."));
    }

    [Test]
    public async Task PlayDiscSet_ATwoDiscSaturnMdsSet_NamesEachGeneratedCue_AndAliasesEachMdf()
    {
        static InfoJsonRom Disc(int n, byte[] mdf) =>
            PsxSolo($"Prism (USA) (Disc {n}).mdf", mdf, isPrimary: n == 1) with
            {
                BaseTitle = "Prism", Regions = ["USA"], Flags = [$"Disc {n}"], Files = [SetFile($"Prism (USA) (Disc {n}).mds", SyntheticMdsBuilder.OneTrack())],
            };
        var (id, _, ids) = await DiscGame(PlayDisc.SaturnSystemId, "Prism", [Disc(1, SetTrack1(80)), Disc(2, SetTrack1(81))],
            ("Prism (USA) (Disc 1).mdf", SetTrack1(80)), ("Prism (USA) (Disc 1).mds", SyntheticMdsBuilder.OneTrack()),
            ("Prism (USA) (Disc 2).mdf", SetTrack1(81)), ("Prism (USA) (Disc 2).mds", SyntheticMdsBuilder.OneTrack()));

        using var response = await _user.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[]
        {
            "Prism (USA).m3u", "Prism (USA) (Disc 1).cue", "disc1-track-01.bin", "Prism (USA) (Disc 2).cue", "disc2-track-01.bin",
        }));
        Assert.That(Encoding.UTF8.GetString(ReadEntry(zip, "Prism (USA).m3u")), Is.EqualTo("Prism (USA) (Disc 1).cue\nPrism (USA) (Disc 2).cue\n"));
        Assert.That(Encoding.UTF8.GetString(ReadEntry(zip, "Prism (USA) (Disc 2).cue")), Does.StartWith("FILE \"disc2-track-01.bin\" BINARY\n"));
        Assert.That(ReadEntry(zip, "disc2-track-01.bin"), Is.EqualTo(SetTrack1(81)));
    }

    [Test]
    public async Task PlayDiscSet_ASaturnSetWithAnNrgDisc_NamesItsGeneratedCue()
    {
        const string title = "Nero Sea";
        var nrg = SyntheticNrgBuilder.Build([new NrgBlock(0, 0, 10 * 2352, Mode: 0x06)]);
        var (id, _, ids) = await DiscGame(PlayDisc.SaturnSystemId, title,
            [OdysseyDisc(1, Umd(82), ".iso", title), OdysseyDisc(2, nrg, ".nrg", title)],
            ($"{title} (USA) (Disc 1).iso", Umd(82)), ($"{title} (USA) (Disc 2).nrg", nrg));

        using var response = await _user.GetAsync(PlayDiscSetUrl(id, ids[0]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var body = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(body, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[]
        {
            "Nero Sea (USA).m3u", "Nero Sea (USA) (Disc 1).iso", "Nero Sea (USA) (Disc 2).cue", "disc2-track-01.bin",
        }));
        Assert.That(Encoding.UTF8.GetString(ReadEntry(zip, "Nero Sea (USA).m3u")), Is.EqualTo("Nero Sea (USA) (Disc 1).iso\nNero Sea (USA) (Disc 2).cue\n"));
        Assert.That(Encoding.UTF8.GetString(ReadEntry(zip, "Nero Sea (USA) (Disc 2).cue")),
            Is.EqualTo("FILE \"disc2-track-01.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"));
        Assert.That(ReadEntry(zip, "disc2-track-01.bin"), Is.EqualTo(nrg[..(10 * 2352)]));
    }
}
