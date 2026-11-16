using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Configuration;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>B2: the generic <c>/api/emulator/cores/{core}</c> routes, with the Genesis routes as their aliases.</summary>
public partial class EmulatorEndpointsTests
{
    private const string GenesisCore = "/api/emulator/cores/genesis_plus_gx";
    private const string PicodriveCore = "/api/emulator/cores/picodrive";
    private const string Snes9xCore = "/api/emulator/cores/snes9x";
    private const string OperaCore = "/api/emulator/cores/opera";
    private const string ViceCore = "/api/emulator/cores/vice_x64sc";

    private static readonly (string Name, byte[] Bytes, string ContentType)[] SyntheticVice =
    [
        ("vice_x64sc-wasm.data", [.. Enumerable.Range(0, 3400).Select(i => (byte)(i * 23 + 5))], "application/octet-stream"),
        ("vice_x64sc-legacy-wasm.data", [.. Enumerable.Range(0, 3200).Select(i => (byte)(i * 29 + 8))], "application/octet-stream"),
        ("vice_x64sc.json", "{\"options\":{\"supportsMouse\":true}}"u8.ToArray(), "application/json"),
    ];

    private string ViceDir => _factory.Settings["Emulator:ViceCorePath"]!;

    private void WriteViceSet(string? except = null)
    {
        foreach (var (name, bytes, _) in SyntheticVice.Where(f => f.Name != except))
            File.WriteAllBytes(Path.Combine(ViceDir, name), bytes);
    }

    private static readonly (string Name, byte[] Bytes, string ContentType)[] SyntheticOpera =
    [
        ("opera-wasm.data", [.. Enumerable.Range(0, 3300).Select(i => (byte)(i * 17 + 3))], "application/octet-stream"),
        ("opera-legacy-wasm.data", [.. Enumerable.Range(0, 3100).Select(i => (byte)(i * 19 + 6))], "application/octet-stream"),
        ("opera.json", "{\"options\":{\"3do\":1}}"u8.ToArray(), "application/json"),
    ];

    private string OperaDir => _factory.Settings["Emulator:OperaCorePath"]!;

    private void WriteOperaSet(string? except = null)
    {
        foreach (var (name, bytes, _) in SyntheticOpera.Where(f => f.Name != except))
            File.WriteAllBytes(Path.Combine(OperaDir, name), bytes);
    }

    private static readonly (string Name, byte[] Bytes, string ContentType)[] SyntheticSnes9x =
    [
        ("snes9x-wasm.data", [.. Enumerable.Range(0, 3072).Select(i => (byte)(i * 5 + 1))], "application/octet-stream"),
        ("snes9x-legacy-wasm.data", [.. Enumerable.Range(0, 2900).Select(i => (byte)(i * 13 + 2))], "application/octet-stream"),
        ("snes9x.json", "{\"options\":{\"snes\":1}}"u8.ToArray(), "application/json"),
    ];

    private string Snes9xDir => _factory.Settings["Emulator:Snes9xCorePath"]!;

    private void WriteSnes9xSet(string? except = null)
    {
        foreach (var (name, bytes, _) in SyntheticSnes9x.Where(f => f.Name != except))
            File.WriteAllBytes(Path.Combine(Snes9xDir, name), bytes);
    }

    private static readonly (string Name, byte[] Bytes, string ContentType)[] SyntheticPicodrive =
    [
        ("picodrive-wasm.data", [.. Enumerable.Range(0, 2048).Select(i => (byte)(i * 3 + 7))], "application/octet-stream"),
        ("picodrive-legacy-wasm.data", [.. Enumerable.Range(0, 2500).Select(i => (byte)(i * 11 + 4))], "application/octet-stream"),
        ("picodrive.json", "{\"options\":{\"x\":1}}"u8.ToArray(), "application/json"),
    ];

    private string PicodriveDir => _factory.Settings["Emulator:PicodriveCorePath"]!;

    private void WritePicodriveSet(string? except = null)
    {
        foreach (var (name, bytes, _) in SyntheticPicodrive.Where(f => f.Name != except))
            File.WriteAllBytes(Path.Combine(PicodriveDir, name), bytes);
    }

    private async Task<HttpStatusCode> HeadAsync(AuthClient client, string url)
    {
        using var response = await client.SendAsync(HttpMethod.Head, url, null, false);
        if (response.StatusCode != HttpStatusCode.Unauthorized && response.StatusCode != HttpStatusCode.Forbidden)
        {
            AssertNoStore(response);
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty, "HEAD is bodyless");
        }
        return response.StatusCode;
    }

    [Test]
    public void PinnedManifest_NamesEachCore()
    {
        var genesis = OperatorCoreManifest.Pinned.Find("genesis_plus_gx")!;

        var picodrive = OperatorCoreManifest.Pinned.Find("picodrive")!;
        var snes9x = OperatorCoreManifest.Pinned.Find("snes9x")!;
        var opera = OperatorCoreManifest.Pinned.Find("opera")!;
        var vice = OperatorCoreManifest.Pinned.Find("vice_x64sc")!;
        var options = new EmulatorOptions
        {
            GenesisCorePath = "/cores/genesis", PicodriveCorePath = "/cores/picodrive", Snes9xCorePath = "/cores/snes9x",
            OperaCorePath = "/cores/opera", ViceCorePath = "/cores/vice",
        };

        Assert.That(OperatorCoreManifest.Pinned.Cores.Select(c => c.Id),
            Is.EqualTo(new[] { "genesis_plus_gx", "picodrive", "snes9x", "opera", "vice_x64sc", "vice_xpet" }));
        Assert.That((genesis.DisplayName, genesis.MismatchCode), Is.EqualTo(("Genesis Plus GX", "GenesisCoreMismatch")));
        Assert.That((picodrive.DisplayName, picodrive.MismatchCode), Is.EqualTo(("PicoDrive", "PicodriveCoreMismatch")));
        Assert.That((snes9x.DisplayName, snes9x.MismatchCode), Is.EqualTo(("Snes9x", "Snes9xCoreMismatch")));
        Assert.That((opera.DisplayName, opera.MismatchCode), Is.EqualTo(("Opera", "OperaCoreMismatch")));
        Assert.That(opera.PathOf(options), Is.EqualTo("/cores/opera"));
        Assert.That((vice.DisplayName, vice.MismatchCode, vice.PathOf(options)), Is.EqualTo(("VICE", "ViceCoreMismatch", "/cores/vice")));
        var pet = OperatorCoreManifest.Pinned.Find("vice_xpet")!;
        Assert.That((pet.DisplayName, pet.MismatchCode, pet.PathOf(options)), Is.EqualTo(("VICE (PET)", "ViceCoreMismatch", "/cores/vice")), "shares the C64 folder");
        Assert.That((genesis.PathOf(options), picodrive.PathOf(options), snes9x.PathOf(options)),
            Is.EqualTo(("/cores/genesis", "/cores/picodrive", "/cores/snes9x")));
        Assert.That(OperatorCoreManifest.Pinned.Find("GENESIS_PLUS_GX"), Is.Null);
        Assert.That(OperatorCoreManifest.Pinned.Find("PicoDrive"), Is.Null);
        Assert.That(OperatorCoreManifest.Pinned.Find("Snes9x"), Is.Null);
        Assert.That(OperatorCoreManifest.Pinned.Find("OPERA"), Is.Null);
        Assert.That(OperatorCoreManifest.Pinned.Find("VICE_X64SC"), Is.Null);
    }

    [Test]
    public void PinnedManifest_HoldsExactlyThePicodrive4_2_3Files()
    {
        Assert.That(OperatorCoreManifest.Pinned.Find(OperatorCoreManifest.Picodrive)!.Files, Is.EqualTo(new[]
        {
            new OperatorCoreFile("picodrive-wasm.data", 1_034_483,
                "bb5d50b8b88111b583977d2f7a16d01a822b3deda9205048d99ecaad2c56d861", "application/octet-stream"),
            new OperatorCoreFile("picodrive-legacy-wasm.data", 1_035_609,
                "cc42b0c95e9edfd0e5c689211e8e85e45f4525be2d814af8ca9331ef90e0620b", "application/octet-stream"),
            new OperatorCoreFile("picodrive.json", 123,
                "2b09f29e095a003c15abf8e5bdfb0eaca96d6c6121183b3bd92ed85e860918d3", "application/json"),
        }));
    }

    [Test]
    public void PinnedManifest_HoldsExactlyTheSnes9x4_2_3Files()
    {
        Assert.That(OperatorCoreManifest.Pinned.Find(OperatorCoreManifest.Snes9x)!.Files, Is.EqualTo(new[]
        {
            new OperatorCoreFile("snes9x-wasm.data", 1_093_765,
                "eaa0bcfce67673809886e50387a80a616b719502175db64c090d04c9d75958ee", "application/octet-stream"),
            new OperatorCoreFile("snes9x-legacy-wasm.data", 1_092_437,
                "7d427a575cefad98ff400493fa1d7e892da63fe7bab68979babd9cea0bfaaf3b", "application/octet-stream"),
            new OperatorCoreFile("snes9x.json", 120,
                "dc7ac963eb7935a7ac78956235ac0b8912ec785c57026336825aa2ed8031b3ad", "application/json"),
        }));
    }

    [Test]
    public async Task Snes9x_AMatchingSet_Is204_AndEachFileIsServedExactly()
    {
        WriteSnes9xSet();

        Assert.That(await HeadAsync(_client, Snes9xCore), Is.EqualTo(HttpStatusCode.NoContent));
        foreach (var (name, bytes, contentType) in SyntheticSnes9x)
        {
            using var response = await _client.GetAsync($"{Snes9xCore}/{name}");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), name);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(contentType), name);
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes), name);
            AssertNoStore(response);
        }
    }

    [Test]
    public async Task Snes9x_AMissingMount_AnIncompleteSet_OrAnEmptyFile_Is404()
    {
        Directory.Delete(Snes9xDir);
        Assert.That(await HeadAsync(_client, Snes9xCore), Is.EqualTo(HttpStatusCode.NotFound), "missing mount");
        await AssertNotFoundAsync($"{Snes9xCore}/snes9x.json");

        Directory.CreateDirectory(Snes9xDir);
        WriteSnes9xSet(except: "snes9x-legacy-wasm.data");
        Assert.That(await HeadAsync(_client, Snes9xCore), Is.EqualTo(HttpStatusCode.NotFound), "incomplete set");
        await AssertNotFoundAsync($"{Snes9xCore}/snes9x-legacy-wasm.data");
        await AssertNotFoundAsync($"{Snes9xCore}/SNES9X.JSON");

        File.WriteAllBytes(Path.Combine(Snes9xDir, "snes9x-legacy-wasm.data"), []);
        Assert.That(await HeadAsync(_client, Snes9xCore), Is.EqualTo(HttpStatusCode.NotFound), "zero-byte file");
        await AssertNotFoundAsync($"{Snes9xCore}/snes9x-legacy-wasm.data");
    }

    [TestCase("wrong length")]
    [TestCase("same-length wrong hash")]
    public async Task Snes9x_AMismatch_Is409Snes9xCoreMismatch(string kind)
    {
        WriteSnes9xSet();
        var path = Path.Combine(Snes9xDir, "snes9x-wasm.data");
        if (kind == "wrong length")
            File.AppendAllText(path, "x");
        else
        {
            var bytes = File.ReadAllBytes(path);
            bytes[100] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
        }

        Assert.That(await HeadAsync(_client, Snes9xCore), Is.EqualTo(HttpStatusCode.Conflict));
        using var response = await _client.GetAsync($"{Snes9xCore}/snes9x-wasm.data");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo(EmulatorEndpoints.Snes9xCoreMismatch));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("The Snes9x core file doesn't match EmulatorJS 4.2.3"));
        AssertNoStore(response);
    }

    [Test]
    public async Task Snes9x_AnonymousIs401_AndAForcedPasswordChangeIs403()
    {
        WriteSnes9xSet();
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var flagged = await SignInAsync("flagged-snes9x", mustChangePassword: true);

        using var anonGet = await anonymous.GetAsync($"{Snes9xCore}/snes9x.json");
        using var flaggedGet = await flagged.GetAsync($"{Snes9xCore}/snes9x.json");

        Assert.That(await HeadAsync(anonymous, Snes9xCore), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await HeadAsync(flagged, Snes9xCore), Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((anonGet.StatusCode, flaggedGet.StatusCode), Is.EqualTo((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden)));
    }

    [Test]
    public async Task Picodrive_AMatchingSet_Is204_AndEachFileIsServedExactly()
    {
        WritePicodriveSet();

        Assert.That(await HeadAsync(_client, PicodriveCore), Is.EqualTo(HttpStatusCode.NoContent));
        foreach (var (name, bytes, contentType) in SyntheticPicodrive)
        {
            using var response = await _client.GetAsync($"{PicodriveCore}/{name}");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), name);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(contentType), name);
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes), name);
            AssertNoStore(response);
        }
    }

    [Test]
    public async Task Picodrive_AMissingMount_OrAnIncompleteSet_Is404()
    {
        Directory.Delete(PicodriveDir);
        Assert.That(await HeadAsync(_client, PicodriveCore), Is.EqualTo(HttpStatusCode.NotFound), "missing mount");
        await AssertNotFoundAsync($"{PicodriveCore}/picodrive.json");

        Directory.CreateDirectory(PicodriveDir);
        WritePicodriveSet(except: "picodrive-wasm.data");
        Assert.That(await HeadAsync(_client, PicodriveCore), Is.EqualTo(HttpStatusCode.NotFound), "incomplete set");
        await AssertNotFoundAsync($"{PicodriveCore}/picodrive-wasm.data");
    }

    [Test]
    public async Task Picodrive_AMismatch_Is409PicodriveCoreMismatch()
    {
        WritePicodriveSet();
        File.AppendAllText(Path.Combine(PicodriveDir, "picodrive-legacy-wasm.data"), "x");

        Assert.That(await HeadAsync(_client, PicodriveCore), Is.EqualTo(HttpStatusCode.Conflict));
        using var response = await _client.GetAsync($"{PicodriveCore}/picodrive-legacy-wasm.data");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo(EmulatorEndpoints.PicodriveCoreMismatch));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("The PicoDrive core file doesn't match EmulatorJS 4.2.3"));
        AssertNoStore(response);
    }

    [Test]
    public async Task Picodrive_ALockedFile_Is500IoError()
    {
        Assume.That(OperatingSystem.IsWindows(), "FileShare.None only blocks other opens on Windows.");
        WritePicodriveSet();
        using var held = new FileStream(Path.Combine(PicodriveDir, "picodrive.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.That(await HeadAsync(_client, PicodriveCore), Is.EqualTo(HttpStatusCode.InternalServerError));
        using var response = await _client.GetAsync($"{PicodriveCore}/picodrive.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("IoError"));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("The PicoDrive core file can't be read"));
    }

    [Test]
    public async Task Cores_OneCoresFiles_DoNotSatisfyTheOther()
    {
        foreach (var (name, bytes, _) in Synthetic)
            File.WriteAllBytes(Path.Combine(PicodriveDir, name), bytes);
        foreach (var (name, bytes, _) in SyntheticPicodrive)
            File.WriteAllBytes(Path.Combine(GenesisDir, name), bytes);

        Assert.That(await HeadAsync(_client, PicodriveCore), Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await HeadAsync(_client, GenesisCore), Is.EqualTo(HttpStatusCode.NotFound));
        await AssertNotFoundAsync($"{PicodriveCore}/genesis_plus_gx.json");
        await AssertNotFoundAsync($"{GenesisCore}/picodrive.json");
    }

    [Test]
    public async Task Cores_BothSetsInOneFolder_EachIsAvailable()
    {
        WriteGenesisSet();
        foreach (var (name, bytes, _) in SyntheticPicodrive)
            File.WriteAllBytes(Path.Combine(GenesisDir, name), bytes);
        WritePicodriveSet();
        foreach (var (name, bytes, _) in SyntheticSnes9x)
            File.WriteAllBytes(Path.Combine(GenesisDir, name), bytes);
        WriteSnes9xSet();

        Assert.That(await HeadAsync(_client, GenesisCore), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await HeadAsync(_client, PicodriveCore), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await HeadAsync(_client, Snes9xCore), Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task Picodrive_ByDefault_ExposesNothing()
    {
        // A default deployment: no Emulator:PicodriveCorePath, the pinned manifest. Files at a would-be mount stay unserved.
        using var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        await factory.StartSharedAsync();
        Directory.CreateDirectory(Path.Combine(factory.Root, "picodrive"));
        File.WriteAllBytes(Path.Combine(factory.Root, "picodrive", "picodrive.json"), SyntheticPicodrive[2].Bytes);
        using var client = await AuthClient.CreateAsync(factory);
        await TestUsers.CreateAsync(factory, "player");
        (await client.LoginAsync("player", TestUsers.Password)).Dispose();

        Assert.That(factory.Services.GetRequiredService<OperatorCoreManifest>(), Is.SameAs(OperatorCoreManifest.Pinned));
        Assert.That(await HeadAsync(client, PicodriveCore), Is.EqualTo(HttpStatusCode.NotFound));
        using var response = await client.GetAsync($"{PicodriveCore}/picodrive.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        AssertNoStore(response);
    }

    [Test]
    public async Task Cores_AMatchingSet_Is204_AndEachFileIsServedExactly()
    {
        WriteGenesisSet();

        Assert.That(await HeadAsync(_client, GenesisCore), Is.EqualTo(HttpStatusCode.NoContent));
        foreach (var (name, bytes, contentType) in Synthetic)
        {
            using var response = await _client.GetAsync($"{GenesisCore}/{name}");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), name);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(contentType), name);
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes), name);
            AssertNoStore(response);
        }
    }

    [Test]
    public async Task Cores_AnIncompleteSet_Is404()
    {
        WriteGenesisSet(except: "genesis_plus_gx.json");

        Assert.That(await HeadAsync(_client, GenesisCore), Is.EqualTo(HttpStatusCode.NotFound));
        await AssertNotFoundAsync($"{GenesisCore}/genesis_plus_gx.json");
    }

    [Test]
    public async Task Cores_AMismatch_Is409_WithTheCoresCodeAndTitle()
    {
        WriteGenesisSet();
        File.WriteAllBytes(Path.Combine(GenesisDir, "genesis_plus_gx.json"), "{\"options\":{} }"u8.ToArray());

        Assert.That(await HeadAsync(_client, GenesisCore), Is.EqualTo(HttpStatusCode.Conflict));
        using var response = await _client.GetAsync($"{GenesisCore}/genesis_plus_gx.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("GenesisCoreMismatch"));
        Assert.That(await response.Content.ReadAsStringAsync(),
            Does.Contain("The Genesis Plus GX core file doesn't match EmulatorJS 4.2.3"));
        AssertNoStore(response);
    }

    [Test]
    public async Task Cores_ALockedFile_Is500IoError()
    {
        Assume.That(OperatingSystem.IsWindows(), "FileShare.None only blocks other opens on Windows.");
        WriteGenesisSet();
        using var held = new FileStream(Path.Combine(GenesisDir, "genesis_plus_gx.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.That(await HeadAsync(_client, GenesisCore), Is.EqualTo(HttpStatusCode.InternalServerError));
        using var response = await _client.GetAsync($"{GenesisCore}/genesis_plus_gx.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("IoError"));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("The Genesis Plus GX core file can't be read"));
    }

    [Test]
    public async Task Cores_Head_Prefers500_Then409_Then404()
    {
        WriteGenesisSet(except: "genesis_plus_gx-legacy-wasm.data");
        var wasm = Path.Combine(GenesisDir, "genesis_plus_gx-wasm.data");
        File.AppendAllText(wasm, "x");
        Assert.That(await HeadAsync(_client, GenesisCore), Is.EqualTo(HttpStatusCode.Conflict), "409 over 404");

        if (!OperatingSystem.IsWindows())
            return; // FileShare.None only blocks other opens on Windows.
        using var held = new FileStream(Path.Combine(GenesisDir, "genesis_plus_gx.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.That(await HeadAsync(_client, GenesisCore), Is.EqualTo(HttpStatusCode.InternalServerError), "500 over 409 and 404");
    }

    [TestCase("unknown")]
    [TestCase("GENESIS_PLUS_GX")]
    [TestCase("Genesis_Plus_Gx")]
    public async Task Cores_AnUnknownOrCaseVariantCore_Is404(string core)
    {
        WriteGenesisSet();

        Assert.That(await HeadAsync(_client, $"/api/emulator/cores/{core}"), Is.EqualTo(HttpStatusCode.NotFound));
        await AssertNotFoundAsync($"/api/emulator/cores/{core}/genesis_plus_gx.json");
    }

    [TestCase("other.data")]
    [TestCase("GENESIS_PLUS_GX.JSON")]
    [TestCase("..%2Fgenesis_plus_gx.json")]
    [TestCase("..%2F..%2Fbios%2Fcolecovision.rom")]
    public async Task Cores_AnUnknownCaseVariantOrTraversalName_Is404(string name)
    {
        WriteGenesisSet();
        File.WriteAllBytes(Path.Combine(GenesisDir, "other.data"), [1, 2, 3]);
        WriteBios("colecovision.rom");

        await AssertNotFoundAsync($"{GenesisCore}/{name}");
    }

    [Test]
    public async Task Cores_TheGenesisRoutes_AreAliases()
    {
        foreach (var state in new[] { "missing", "matching", "mismatch" })
        {
            ResetGenesisDir();
            if (state != "missing")
                WriteGenesisSet();
            if (state == "mismatch")
                File.AppendAllText(Path.Combine(GenesisDir, "genesis_plus_gx.json"), " ");

            Assert.That(await HeadAsync(_client, GenesisHead), Is.EqualTo(await HeadAsync(_client, GenesisCore)), state);
            foreach (var (name, _, _) in Synthetic)
            {
                using var alias = await _client.GetAsync($"/api/emulator/genesis/{name}");
                using var generic = await _client.GetAsync($"{GenesisCore}/{name}");
                Assert.That(alias.StatusCode, Is.EqualTo(generic.StatusCode), $"{state} {name}");
                // Problem bodies carry the request path, so errors compare by code and files by bytes.
                if (alias.StatusCode == HttpStatusCode.OK)
                    Assert.That(await alias.Content.ReadAsByteArrayAsync(), Is.EqualTo(await generic.Content.ReadAsByteArrayAsync()),
                        $"{state} {name}");
                else
                    Assert.That(await AuthClient.CodeAsync(alias), Is.EqualTo(await AuthClient.CodeAsync(generic)), $"{state} {name}");
            }
        }
    }

    [Test]
    public async Task Cores_AnonymousIs401_AndAForcedPasswordChangeIs403()
    {
        WriteGenesisSet();
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var flagged = await SignInAsync("flagged-cores", mustChangePassword: true);

        using var anonGet = await anonymous.GetAsync($"{GenesisCore}/genesis_plus_gx.json");
        using var flaggedGet = await flagged.GetAsync($"{GenesisCore}/genesis_plus_gx.json");

        Assert.That(await HeadAsync(anonymous, GenesisCore), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await HeadAsync(flagged, GenesisCore), Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((anonGet.StatusCode, flaggedGet.StatusCode), Is.EqualTo((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden)));
    }

    // 3DO B5: the operator-supplied Opera files behave exactly as Snes9x's.

    [Test]
    public void PinnedManifest_HoldsExactlyTheOpera4_2_3Files()
    {
        Assert.That(OperatorCoreManifest.Pinned.Find(OperatorCoreManifest.Opera)!.Files, Is.EqualTo(new[]
        {
            new OperatorCoreFile("opera-wasm.data", 854_147,
                "3e737f4f739814c12c017a5f26cb1e43bdfe3ac6f2d3bf6e8972633df49e33d4", "application/octet-stream"),
            new OperatorCoreFile("opera-legacy-wasm.data", 853_889,
                "41d3a7336cd304693c86974b50df5f7a90d9557077b01e0a1e928b3a38fe4f86", "application/octet-stream"),
            new OperatorCoreFile("opera.json", 119,
                "0245b492b516ef4b1c0490e3f05dbeb1d74074ea767cd54fc34747e31b2a0203", "application/json"),
        }));
    }

    [Test]
    public async Task Opera_AMatchingSet_Is204_AndEachFileIsServedExactly()
    {
        WriteOperaSet();

        Assert.That(await HeadAsync(_client, OperaCore), Is.EqualTo(HttpStatusCode.NoContent));
        foreach (var (name, bytes, contentType) in SyntheticOpera)
        {
            using var response = await _client.GetAsync($"{OperaCore}/{name}");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), name);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(contentType), name);
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes), name);
            AssertNoStore(response);
        }
    }

    [Test]
    public async Task Opera_AMissingMount_AnIncompleteSet_OrAnEmptyFile_Is404()
    {
        Directory.Delete(OperaDir);
        Assert.That(await HeadAsync(_client, OperaCore), Is.EqualTo(HttpStatusCode.NotFound), "missing mount");
        await AssertNotFoundAsync($"{OperaCore}/opera.json");

        Directory.CreateDirectory(OperaDir);
        WriteOperaSet(except: "opera-legacy-wasm.data");
        Assert.That(await HeadAsync(_client, OperaCore), Is.EqualTo(HttpStatusCode.NotFound), "incomplete set");
        await AssertNotFoundAsync($"{OperaCore}/opera-legacy-wasm.data");
        await AssertNotFoundAsync($"{OperaCore}/OPERA.JSON");

        File.WriteAllBytes(Path.Combine(OperaDir, "opera-legacy-wasm.data"), []);
        Assert.That(await HeadAsync(_client, OperaCore), Is.EqualTo(HttpStatusCode.NotFound), "zero-byte file");
        await AssertNotFoundAsync($"{OperaCore}/opera-legacy-wasm.data");
    }

    [TestCase("wrong length")]
    [TestCase("same-length wrong hash")]
    public async Task Opera_AMismatch_Is409OperaCoreMismatch(string kind)
    {
        WriteOperaSet();
        var path = Path.Combine(OperaDir, "opera-wasm.data");
        if (kind == "wrong length")
            File.AppendAllText(path, "x");
        else
        {
            var bytes = File.ReadAllBytes(path);
            bytes[100] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
        }

        Assert.That(await HeadAsync(_client, OperaCore), Is.EqualTo(HttpStatusCode.Conflict));
        using var response = await _client.GetAsync($"{OperaCore}/opera-wasm.data");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo(EmulatorEndpoints.OperaCoreMismatch));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("The Opera core file doesn't match EmulatorJS 4.2.3"));
        AssertNoStore(response);
    }

    [Test]
    public async Task Opera_ALockedFile_Is500IoError()
    {
        Assume.That(OperatingSystem.IsWindows(), "FileShare.None only blocks other opens on Windows.");
        WriteOperaSet();
        using var held = new FileStream(Path.Combine(OperaDir, "opera.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.That(await HeadAsync(_client, OperaCore), Is.EqualTo(HttpStatusCode.InternalServerError));
        using var response = await _client.GetAsync($"{OperaCore}/opera.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("IoError"));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("The Opera core file can't be read"));
    }

    [Test]
    public async Task Opera_AnonymousIs401_AndAForcedPasswordChangeIs403()
    {
        WriteOperaSet();
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var flagged = await SignInAsync("flagged-opera", mustChangePassword: true);

        using var anonGet = await anonymous.GetAsync($"{OperaCore}/opera.json");
        using var flaggedGet = await flagged.GetAsync($"{OperaCore}/opera.json");

        Assert.That(await HeadAsync(anonymous, OperaCore), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await HeadAsync(flagged, OperaCore), Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((anonGet.StatusCode, flaggedGet.StatusCode), Is.EqualTo((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden)));
    }

    [Test]
    public async Task Opera_SharingAFolderWithTheOtherCores_IsAvailable()
    {
        WriteGenesisSet();
        foreach (var (name, bytes, _) in SyntheticOpera)
            File.WriteAllBytes(Path.Combine(GenesisDir, name), bytes);
        WriteOperaSet();

        Assert.That(await HeadAsync(_client, GenesisCore), Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(await HeadAsync(_client, OperaCore), Is.EqualTo(HttpStatusCode.NoContent));
    }

    // C64 B7: the operator-supplied VICE files behave exactly as Opera's.

    [Test]
    public void PinnedManifest_HoldsExactlyTheVice4_2_3Files()
    {
        Assert.That(OperatorCoreManifest.Pinned.Find(OperatorCoreManifest.Vice)!.Files, Is.EqualTo(new[]
        {
            new OperatorCoreFile("vice_x64sc-wasm.data", 1_523_457,
                "77f58884c81b58721cbc4754ffc5574838219258e9390630c352a494a3f335ab", "application/octet-stream"),
            new OperatorCoreFile("vice_x64sc-legacy-wasm.data", 1_523_390,
                "8a22bc81c0dd7104cd23ceec5117a9f47ca9503e280e4fbc9d0346f51b75253e", "application/octet-stream"),
            new OperatorCoreFile("vice_x64sc.json", 147,
                "5c50bc4ff5546a2c6fc4bb7949efd200ea0ac6b4723713e742fb659228e9a3aa", "application/json"),
        }));
    }

    [Test]
    public void PinnedManifest_HoldsExactlyTheVicePet4_2_3Files()
    {
        Assert.That(OperatorCoreManifest.Pinned.Find(OperatorCoreManifest.VicePet)!.Files, Is.EqualTo(new[]
        {
            new OperatorCoreFile("vice_xpet-wasm.data", 1_368_677,
                "eef0d83f7c1af43d51c3384a07db4196ea7c2bbe1292e9e52fade3d3107513c5", "application/octet-stream"),
            new OperatorCoreFile("vice_xpet-legacy-wasm.data", 1_368_450,
                "98ac6fbb90824a77ae6480a431c465d41e8cecce39394e036648d57e86297361", "application/octet-stream"),
            new OperatorCoreFile("vice_xpet.json", 123,
                "9bf5ec80ab1f58dc7cdccd73f0141385b030f262ce20aaae4403369dd7061301", "application/json"),
        }));
    }

    [Test]
    public async Task Vice_AMatchingSet_Is204_AndEachFileIsServedExactly()
    {
        WriteViceSet();

        Assert.That(await HeadAsync(_client, ViceCore), Is.EqualTo(HttpStatusCode.NoContent));
        foreach (var (name, bytes, contentType) in SyntheticVice)
        {
            using var response = await _client.GetAsync($"{ViceCore}/{name}");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), name);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(contentType), name);
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes), name);
            AssertNoStore(response);
        }
    }

    [Test]
    public async Task Vice_AMissingMount_AnIncompleteSet_OrAnEmptyFile_Is404()
    {
        Directory.Delete(ViceDir);
        Assert.That(await HeadAsync(_client, ViceCore), Is.EqualTo(HttpStatusCode.NotFound), "missing mount");
        await AssertNotFoundAsync($"{ViceCore}/vice_x64sc.json");

        Directory.CreateDirectory(ViceDir);
        WriteViceSet(except: "vice_x64sc-legacy-wasm.data");
        Assert.That(await HeadAsync(_client, ViceCore), Is.EqualTo(HttpStatusCode.NotFound), "incomplete set");
        await AssertNotFoundAsync($"{ViceCore}/vice_x64sc-legacy-wasm.data");
        await AssertNotFoundAsync($"{ViceCore}/VICE_X64SC.JSON");

        File.WriteAllBytes(Path.Combine(ViceDir, "vice_x64sc-legacy-wasm.data"), []);
        Assert.That(await HeadAsync(_client, ViceCore), Is.EqualTo(HttpStatusCode.NotFound), "zero-byte file");
        await AssertNotFoundAsync($"{ViceCore}/vice_x64sc-legacy-wasm.data");
    }

    [TestCase("wrong length")]
    [TestCase("same-length wrong hash")]
    public async Task Vice_AMismatch_Is409ViceCoreMismatch(string kind)
    {
        WriteViceSet();
        var path = Path.Combine(ViceDir, "vice_x64sc-wasm.data");
        if (kind == "wrong length")
            File.AppendAllText(path, "x");
        else
        {
            var bytes = File.ReadAllBytes(path);
            bytes[100] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
        }

        Assert.That(await HeadAsync(_client, ViceCore), Is.EqualTo(HttpStatusCode.Conflict));
        using var response = await _client.GetAsync($"{ViceCore}/vice_x64sc-wasm.data");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo(EmulatorEndpoints.ViceCoreMismatch));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("The VICE core file doesn't match EmulatorJS 4.2.3"));
        AssertNoStore(response);
    }
}
