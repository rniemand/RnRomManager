using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Api.Endpoints;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Genesis B4: <c>GET /api/emulator/genesis/{name}</c> and <c>HEAD /api/emulator/genesis</c>.</summary>
public partial class EmulatorEndpointsTests
{
    private static readonly (string Name, byte[] Bytes, string ContentType)[] Synthetic =
    [
        ("genesis_plus_gx-wasm.data", [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 5 + 1))], "application/octet-stream"),
        ("genesis_plus_gx-legacy-wasm.data", [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 9 + 2))], "application/octet-stream"),
        ("genesis_plus_gx.json", "{\"options\":{}}"u8.ToArray(), "application/json"),
    ];

    private static readonly GenesisCoreManifest SyntheticManifest = new(
        [.. Synthetic.Select(f => new GenesisCoreFile(f.Name, f.Bytes.Length, Convert.ToHexStringLower(SHA256.HashData(f.Bytes)), f.ContentType))]);

    private const string GenesisHead = "/api/emulator/genesis";

    private string GenesisDir => _factory.Settings["Emulator:GenesisCorePath"]!;

    private void ResetGenesisDir()
    {
        if (Directory.Exists(GenesisDir))
        {
            foreach (var entry in new DirectoryInfo(GenesisDir).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo dir)
                    dir.Delete(recursive: !dir.Attributes.HasFlag(FileAttributes.ReparsePoint));
                else
                    entry.Delete();
            }
        }
        Directory.CreateDirectory(GenesisDir);
    }

    private void WriteGenesisSet(string? except = null)
    {
        foreach (var (name, bytes, _) in Synthetic.Where(f => f.Name != except))
            File.WriteAllBytes(Path.Combine(GenesisDir, name), bytes);
    }

    private async Task<HttpStatusCode> HeadGenesisAsync(AuthClient client)
    {
        using var response = await client.SendAsync(HttpMethod.Head, GenesisHead, null, false);
        if (response.StatusCode != HttpStatusCode.Unauthorized && response.StatusCode != HttpStatusCode.Forbidden)
        {
            AssertNoStore(response);
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty, "HEAD is bodyless");
        }
        return response.StatusCode;
    }

    [Test]
    public void PinnedManifest_HoldsExactlyTheThree4_2_3Files()
    {
        Assert.That(GenesisCoreManifest.Pinned.Files, Is.EqualTo(new[]
        {
            new GenesisCoreFile("genesis_plus_gx-wasm.data", 1_203_661,
                "190297a6f86757405090f1a2266f67dfe1a570a528c583434ed3641a5664f768", "application/octet-stream"),
            new GenesisCoreFile("genesis_plus_gx-legacy-wasm.data", 1_204_803,
                "2a1edeb68d7ec882149ed3cd5d3aa95a6ac231ebabaf82cdb56175a8b62967cc", "application/octet-stream"),
            new GenesisCoreFile("genesis_plus_gx.json", 129,
                "5936a8ce8d7f010d5bfdd8c3bba2b2414f103b3a703121e56f2724b24dbe7ff3", "application/json"),
        }));
    }

    [Test]
    public async Task Genesis_AMatchingSet_IsAvailable_AndEachFileIsServedExactly_EvenForARangeRequest()
    {
        WriteGenesisSet();

        Assert.That(await HeadGenesisAsync(_client), Is.EqualTo(HttpStatusCode.NoContent));
        foreach (var (name, bytes, contentType) in Synthetic)
        {
            using var response = await _client.SendAsync(HttpMethod.Get, $"/api/emulator/genesis/{name}", null, false,
                h => h.Range = new RangeHeaderValue(0, 9));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), name);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(contentType), name);
            Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(bytes.Length), name);
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes), name);
            AssertNoStore(response);
        }
    }

    [Test]
    public async Task Genesis_AMissingMount_OrAnIncompleteSet_Is404()
    {
        Directory.Delete(GenesisDir);
        Assert.That(await HeadGenesisAsync(_client), Is.EqualTo(HttpStatusCode.NotFound), "missing mount");
        await AssertNotFoundAsync("/api/emulator/genesis/genesis_plus_gx.json");

        Directory.CreateDirectory(GenesisDir);
        WriteGenesisSet(except: "genesis_plus_gx-legacy-wasm.data");
        Assert.That(await HeadGenesisAsync(_client), Is.EqualTo(HttpStatusCode.NotFound), "incomplete set");
        await AssertNotFoundAsync("/api/emulator/genesis/genesis_plus_gx-legacy-wasm.data");
    }

    [TestCase("genesis_plus_gx-wasm.data")]
    [TestCase("genesis_plus_gx-legacy-wasm.data")]
    [TestCase("genesis_plus_gx.json")]
    public async Task Genesis_AFileOfTheWrongLength_OrHash_Is409GenesisCoreMismatch(string name)
    {
        var original = Synthetic.Single(f => f.Name == name).Bytes;
        WriteGenesisSet();
        var path = Path.Combine(GenesisDir, name);
        foreach (var (label, bytes) in new[]
        {
            ("wrong length", original.Append((byte)0).ToArray()),
            ("wrong hash", original.Select((b, i) => i == 0 ? (byte)(b ^ 0xFF) : b).ToArray()),
        })
        {
            File.WriteAllBytes(path, bytes);

            Assert.That(await HeadGenesisAsync(_client), Is.EqualTo(HttpStatusCode.Conflict), label);
            using var response = await _client.GetAsync($"/api/emulator/genesis/{name}");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), label);
            Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo(EmulatorEndpoints.GenesisCoreMismatch), label);
            AssertNoStore(response);
        }
    }

    [TestCase("other.data")]
    [TestCase("GENESIS_PLUS_GX.JSON")]
    [TestCase("genesis_plus_gx.json.bak")]
    [TestCase("..%2Fgenesis_plus_gx.json")]
    [TestCase("colecovision.rom")]
    public async Task Genesis_AnUnlistedName_Is404(string name)
    {
        WriteGenesisSet();
        File.WriteAllBytes(Path.Combine(GenesisDir, "other.data"), [1, 2, 3]);
        WriteBios("colecovision.rom");

        await AssertNotFoundAsync($"/api/emulator/genesis/{name}");
    }

    [Test]
    public async Task Genesis_AnEmptyFile_OrADirectory_Is404()
    {
        WriteGenesisSet();
        File.WriteAllBytes(Path.Combine(GenesisDir, "genesis_plus_gx.json"), []);
        Assert.That(await HeadGenesisAsync(_client), Is.EqualTo(HttpStatusCode.NotFound), "empty");
        await AssertNotFoundAsync("/api/emulator/genesis/genesis_plus_gx.json");

        File.Delete(Path.Combine(GenesisDir, "genesis_plus_gx.json"));
        Directory.CreateDirectory(Path.Combine(GenesisDir, "genesis_plus_gx.json"));
        Assert.That(await HeadGenesisAsync(_client), Is.EqualTo(HttpStatusCode.NotFound), "directory");
        await AssertNotFoundAsync("/api/emulator/genesis/genesis_plus_gx.json");
    }

    [Test]
    public async Task Genesis_AFileSymlink_Is404()
    {
        WriteGenesisSet(except: "genesis_plus_gx.json");
        var target = Path.Combine(_factory.Settings["Paths:Cache"]!, "genesis_plus_gx.json");
        File.WriteAllBytes(target, Synthetic[2].Bytes);
        if (!TestLinks.TryLinkFile(Path.Combine(GenesisDir, "genesis_plus_gx.json"), target))
            Assert.Ignore("This account can't create file symlinks.");

        Assert.That(await HeadGenesisAsync(_client), Is.EqualTo(HttpStatusCode.NotFound));
        await AssertNotFoundAsync("/api/emulator/genesis/genesis_plus_gx.json");
    }

    [Test]
    public async Task Genesis_AmbiguousCaseVariants_Are404()
    {
        WriteGenesisSet();
        if (File.Exists(Path.Combine(GenesisDir, "GENESIS_PLUS_GX.JSON")))
            Assert.Ignore("Needs a case-sensitive filesystem.");
        File.WriteAllBytes(Path.Combine(GenesisDir, "GENESIS_PLUS_GX.JSON"), Synthetic[2].Bytes);

        Assert.That(await HeadGenesisAsync(_client), Is.EqualTo(HttpStatusCode.NotFound));
        await AssertNotFoundAsync("/api/emulator/genesis/genesis_plus_gx.json");
    }

    [Test]
    public async Task Genesis_ALockedFile_Is500()
    {
        Assume.That(OperatingSystem.IsWindows(), "FileShare.None only blocks other opens on Windows.");
        WriteGenesisSet();
        using var held = new FileStream(Path.Combine(GenesisDir, "genesis_plus_gx.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.That(await HeadGenesisAsync(_client), Is.EqualTo(HttpStatusCode.InternalServerError));
        using var response = await _client.GetAsync("/api/emulator/genesis/genesis_plus_gx.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("IoError"));
        AssertNoStore(response);
    }

    [Test]
    public async Task Genesis_AnonymousIs401_AndAForcedPasswordChangeIs403()
    {
        WriteGenesisSet();
        using var anonymous = await AuthClient.CreateAsync(_factory);
        using var flagged = await SignInAsync("flagged-genesis", mustChangePassword: true);

        using var anonGet = await anonymous.GetAsync("/api/emulator/genesis/genesis_plus_gx.json");
        using var flaggedGet = await flagged.GetAsync("/api/emulator/genesis/genesis_plus_gx.json");

        Assert.That(await HeadGenesisAsync(anonymous), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await HeadGenesisAsync(flagged), Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((anonGet.StatusCode, flaggedGet.StatusCode), Is.EqualTo((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden)));
    }

    [Test]
    public async Task Genesis_ByDefault_ExposesNothing()
    {
        // A default deployment: no Emulator:GenesisCorePath, the pinned manifest. Files at a would-be mount stay unserved.
        using var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        await factory.StartSharedAsync();
        Directory.CreateDirectory(Path.Combine(factory.Root, "genesis"));
        File.WriteAllBytes(Path.Combine(factory.Root, "genesis", "genesis_plus_gx.json"), Synthetic[2].Bytes);
        using var client = await AuthClient.CreateAsync(factory);
        await TestUsers.CreateAsync(factory, "player");
        (await client.LoginAsync("player", TestUsers.Password)).Dispose();

        Assert.That(factory.Services.GetRequiredService<GenesisCoreManifest>(), Is.SameAs(GenesisCoreManifest.Pinned));
        Assert.That(await HeadGenesisAsync(client), Is.EqualTo(HttpStatusCode.NotFound));
        using var response = await client.GetAsync("/api/emulator/genesis/genesis_plus_gx.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        AssertNoStore(response);
    }
}
