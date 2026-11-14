using System.Net;
using RnRomManager.Api.Endpoints;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>B2: the generic <c>/api/emulator/cores/{core}</c> routes, with the Genesis routes as their aliases.</summary>
public partial class EmulatorEndpointsTests
{
    private const string GenesisCore = "/api/emulator/cores/genesis_plus_gx";

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

        Assert.That(OperatorCoreManifest.Pinned.Cores.Select(c => c.Id), Is.EqualTo(new[] { "genesis_plus_gx" }));
        Assert.That((genesis.DisplayName, genesis.MismatchCode), Is.EqualTo(("Genesis Plus GX", "GenesisCoreMismatch")));
        Assert.That(genesis.PathOf(new EmulatorOptions { GenesisCorePath = "/cores/genesis" }), Is.EqualTo("/cores/genesis"));
        Assert.That(OperatorCoreManifest.Pinned.Find("GENESIS_PLUS_GX"), Is.Null);
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
