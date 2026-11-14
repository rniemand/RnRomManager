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
        var options = new EmulatorOptions
        {
            GenesisCorePath = "/cores/genesis", PicodriveCorePath = "/cores/picodrive", Snes9xCorePath = "/cores/snes9x",
        };

        Assert.That(OperatorCoreManifest.Pinned.Cores.Select(c => c.Id),
            Is.EqualTo(new[] { "genesis_plus_gx", "picodrive", "snes9x" }));
        Assert.That((genesis.DisplayName, genesis.MismatchCode), Is.EqualTo(("Genesis Plus GX", "GenesisCoreMismatch")));
        Assert.That((picodrive.DisplayName, picodrive.MismatchCode), Is.EqualTo(("PicoDrive", "PicodriveCoreMismatch")));
        Assert.That((snes9x.DisplayName, snes9x.MismatchCode), Is.EqualTo(("Snes9x", "Snes9xCoreMismatch")));
        Assert.That((genesis.PathOf(options), picodrive.PathOf(options), snes9x.PathOf(options)),
            Is.EqualTo(("/cores/genesis", "/cores/picodrive", "/cores/snes9x")));
        Assert.That(OperatorCoreManifest.Pinned.Find("GENESIS_PLUS_GX"), Is.Null);
        Assert.That(OperatorCoreManifest.Pinned.Find("PicoDrive"), Is.Null);
        Assert.That(OperatorCoreManifest.Pinned.Find("Snes9x"), Is.Null);
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
}
