using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>
/// B3: <c>GET /api/emulator/bios/{name}</c> against MariaDB and a temp <c>Paths:Bios</c> folder; Genesis B4 in the
/// <c>.Genesis</c> part, against a temp <c>Emulator:GenesisCorePath</c> and a synthetic manifest.
/// </summary>
[Category("Integration")]
public partial class EmulatorEndpointsTests
{
    private static readonly byte[] Bios = [.. Enumerable.Range(0, 8192).Select(i => (byte)(i * 7))];

    private ApiFactory _factory = null!;
    private AuthClient _client = null!;

    private string BiosDir => _factory.Settings["Paths:Bios"]!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => services.AddSingleton(SyntheticManifest),
        };
        _factory.Settings["Emulator:GenesisCorePath"] = Path.Combine(_factory.Root, "genesis");
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public async Task SetUp()
    {
        await _factory.ResetAsync();
        foreach (var entry in new DirectoryInfo(BiosDir).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo dir)
                dir.Delete(recursive: !dir.Attributes.HasFlag(FileAttributes.ReparsePoint));
            else
                entry.Delete();
        }
        ResetGenesisDir();
        _client = await SignInAsync("player");
    }

    [TearDown]
    public void TearDown() => _client?.Dispose();

    private async Task<AuthClient> SignInAsync(string username, bool mustChangePassword = false)
    {
        var client = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, username, mustChangePassword: mustChangePassword);
        (await client.LoginAsync(username, TestUsers.Password)).Dispose();
        return client;
    }

    private string WriteBios(string name, byte[]? bytes = null)
    {
        var path = Path.Combine(BiosDir, name);
        File.WriteAllBytes(path, bytes ?? Bios);
        return path;
    }

    private static void AssertNoStore(HttpResponseMessage response) =>
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True, "Cache-Control: no-store");

    private async Task AssertNotFoundAsync(string url)
    {
        using var response = await _client.GetAsync(url);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), url);
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("NotFound"), url);
        AssertNoStore(response);
    }

    [Test]
    public async Task User_GetsTheExactBytes_UncachedAsOctetStream()
    {
        WriteBios("colecovision.rom");

        using var response = await _client.GetAsync("/api/emulator/bios/colecovision.rom");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Bios));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        AssertNoStore(response);
    }

    [Test]
    public async Task RangeRequest_GetsTheWholeFile()
    {
        WriteBios("colecovision.rom");

        using var response = await _client.SendAsync(HttpMethod.Get, "/api/emulator/bios/colecovision.rom", null, false,
            h => h.Range = new RangeHeaderValue(0, 9));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Bios));
    }

    [Test]
    public async Task Name_IsMatchedIgnoringCase()
    {
        WriteBios("ColecoVision.ROM");

        using var response = await _client.GetAsync("/api/emulator/bios/colecovision.rom");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Bios));
    }

    [Test]
    public Task MissingFile_Is404() => AssertNotFoundAsync("/api/emulator/bios/colecovision.rom");

    [Test]
    public Task EmptyFile_Is404()
    {
        WriteBios("colecovision.rom", []);
        return AssertNotFoundAsync("/api/emulator/bios/colecovision.rom");
    }

    [TestCase("..%2Fx")]
    [TestCase(".hidden")]
    [TestCase("bios.rom%0A")]
    [TestCase("bios%20file.rom")]
    public Task MalformedName_Is404(string name)
    {
        WriteBios(".hidden");
        WriteBios("bios.rom");
        return AssertNotFoundAsync($"/api/emulator/bios/{name}");
    }

    [Test]
    public Task SixtyFiveCharacterName_Is404()
    {
        var name = new string('a', 61) + ".rom";
        WriteBios(name);
        return AssertNotFoundAsync($"/api/emulator/bios/{name}");
    }

    [Test]
    public async Task SixtyFourCharacterName_IsServed()
    {
        var name = new string('a', 60) + ".rom";
        WriteBios(name);

        using var response = await _client.GetAsync($"/api/emulator/bios/{name}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public Task TrailingDot_OnAnExistingName_Is404()
    {
        WriteBios("colecovision.rom");
        return AssertNotFoundAsync("/api/emulator/bios/colecovision.rom.");
    }

    [Test]
    public Task Directory_Is404()
    {
        Directory.CreateDirectory(Path.Combine(BiosDir, "colecovision.rom"));
        return AssertNotFoundAsync("/api/emulator/bios/colecovision.rom");
    }

    [Test]
    public Task FileSymlink_Is404()
    {
        var target = Path.Combine(_factory.Settings["Paths:Cache"]!, "outside.rom");
        File.WriteAllBytes(target, Bios);
        if (!TestLinks.TryLinkFile(Path.Combine(BiosDir, "colecovision.rom"), target))
            Assert.Ignore("This account can't create file symlinks.");
        return AssertNotFoundAsync("/api/emulator/bios/colecovision.rom");
    }

    [Test]
    public Task CaseCollision_Is404()
    {
        WriteBios("colecovision.rom");
        if (File.Exists(Path.Combine(BiosDir, "COLECOVISION.ROM")))
            Assert.Ignore("Needs a case-sensitive filesystem.");
        WriteBios("COLECOVISION.ROM");
        return AssertNotFoundAsync("/api/emulator/bios/colecovision.rom");
    }

    [Test]
    public async Task LockedFile_Is500IoError()
    {
        Assume.That(OperatingSystem.IsWindows(), "FileShare.None only blocks other opens on Windows.");
        var path = WriteBios("colecovision.rom");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        using var response = await _client.GetAsync("/api/emulator/bios/colecovision.rom");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("IoError"));
        AssertNoStore(response);
    }

    [Test]
    public async Task Anonymous_Get_Is401()
    {
        WriteBios("colecovision.rom");
        using var anonymous = await AuthClient.CreateAsync(_factory);

        using var response = await anonymous.GetAsync("/api/emulator/bios/colecovision.rom");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("Unauthorized"));
    }

    [Test]
    public async Task Head_IsUnmapped_404ForUserAndAnonymous()
    {
        WriteBios("colecovision.rom");
        using var anonymous = await AuthClient.CreateAsync(_factory);

        using var user = await _client.SendAsync(HttpMethod.Head, "/api/emulator/bios/colecovision.rom", null, false);
        using var anon = await anonymous.SendAsync(HttpMethod.Head, "/api/emulator/bios/colecovision.rom", null, false);

        Assert.That((user.StatusCode, anon.StatusCode), Is.EqualTo((HttpStatusCode.NotFound, HttpStatusCode.NotFound)));
    }

    [Test]
    public async Task MustChangePassword_BlocksGetAndHead()
    {
        WriteBios("colecovision.rom");
        using var flagged = await SignInAsync("flagged", mustChangePassword: true);

        using var get = await flagged.GetAsync("/api/emulator/bios/colecovision.rom");
        using var head = await flagged.SendAsync(HttpMethod.Head, "/api/emulator/bios/colecovision.rom", null, false);

        Assert.That((get.StatusCode, head.StatusCode), Is.EqualTo((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)));
        Assert.That(await AuthClient.CodeAsync(get), Is.EqualTo("PasswordChangeRequired"));
    }
}
