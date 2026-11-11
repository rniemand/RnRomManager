using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>
/// Spec G4 for metadata ZIP reads: a ZIP of an external system, or one sitting in an external system's folder, is
/// only read through the checked open, so a ZIP behind a link reads as missing.
/// </summary>
[Category("Integration")]
public class ExternalZipReadTests
{
    private const string RomName = "Orbit [v0].nsp";

    private ApiFactory _factory = null!;
    private AuthClient _user = null!;
    private string _outside = null!;
    private string? _link;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync())
        {
            TestServices = services => services.AddSystemHandler<ExternalTestHandler>(),
        };
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        _user = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "plain");
        (await _user.LoginAsync("plain", TestUsers.Password)).Dispose();
        _outside = Directory.CreateDirectory(Path.Combine(_factory.Root, "outside")).FullName;
    }

    [TearDown]
    public void TearDown()
    {
        if (_link is not null)
            TestLinks.RemoveDirectoryLink(_link); // or the factory's cleanup would follow it
        _link = null; // NUnit reuses this instance for the next test
        _user.Dispose();
        _factory.Dispose();
    }

    /// <summary>Mirrors a one-ROM game of <paramref name="system"/> at <paramref name="zipPath"/> and writes its ZIP into <paramref name="folder"/>.</summary>
    private async Task<Guid> Game(string system, string zipPath, string folder)
    {
        var rom = TestLibrary.Rom(RomName, [1, 2, 3, 4]);
        if (system == ExternalTestHandler.SystemId)
            rom = rom with { ExternalFileName = "Orbit." + RomName };
        var game = TestLibrary.Game("Orbit", rom) with { System = system };
        await _factory.Services.GetRequiredService<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, zipPath, 100, DateTime.UtcNow), default);

        using var zip = ZipFile.Open(Path.Combine(Directory.CreateDirectory(folder).FullName, "Orbit.zip"), ZipArchiveMode.Create);
        foreach (var (name, text) in new[] { ("info.json", "{}"), (LibraryZip.NoteEntry(RomName), "a note"), (LibraryZip.RomEntry(RomName), "rom!") })
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        return game.GameId;
    }

    private async Task<(HttpStatusCode Status, bool HasNote, long RomId)> Detail(Guid id)
    {
        using var response = await _user.GetAsync($"/api/games/{id}");
        var rom = (await AuthClient.JsonAsync(response)).GetProperty("roms")[0];
        return (response.StatusCode, rom.GetProperty("hasNote").GetBoolean(), rom.GetProperty("id").GetInt64());
    }

    private static string[] ZipReads(Guid id) =>
    [
        $"/api/games/{id}/download", $"/api/games/{id}/info-json", $"/api/games/{id}/zip-entries",
        $"/api/games/{id}/zip-entries/download?path=info.json",
    ];

    [TestCase(ExternalTestHandler.SystemId, TestName = "LinkedZip_Is404_AndListsNoNotes external game")]
    [TestCase("gb", TestName = "LinkedZip_Is404_AndListsNoNotes embedded game in an external folder")]
    public async Task LinkedZip_Is404_AndListsNoNotes(string system)
    {
        var id = await Game(system, "ext/linked/Orbit.zip", _outside);
        var link = Path.Combine(_factory.Root, "roms", "ext", "linked");
        Assert.That(TestLinks.TryLinkDirectory(link, _outside), Is.True, "this account can't create a directory link");
        _link = link;

        var detail = await Detail(id);
        Assert.That((detail.Status, detail.HasNote), Is.EqualTo((HttpStatusCode.OK, false)), "the note listing falls back to no notes");
        string[] urls = system == "gb" ? [.. ZipReads(id), $"/api/games/{id}/roms/{detail.RomId}/download"] : ZipReads(id);
        foreach (var url in urls)
        {
            using var response = await _user.GetAsync(url);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), url);
            Assert.That(await AuthClient.CodeAsync(response), Is.EqualTo("NotFound"), url);
        }
    }

    [TestCase(ExternalTestHandler.SystemId, "ext/O/Orbit.zip", TestName = "PlainZip_IsRead external game")]
    [TestCase("gb", "ext/O/Orbit.zip", TestName = "PlainZip_IsRead embedded game in an external folder")]
    [TestCase("gb", "gb/O/Orbit.zip", TestName = "PlainZip_IsRead embedded game")]
    public async Task PlainZip_IsRead(string system, string zipPath)
    {
        var folder = Path.Combine(_factory.Root, "roms", Path.GetDirectoryName(zipPath)!);
        var id = await Game(system, zipPath, folder);

        Assert.That((await Detail(id)).HasNote, Is.True);
        foreach (var url in ZipReads(id))
        {
            using var response = await _user.GetAsync(url);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
        }
        using var download = await _user.GetAsync($"/api/games/{id}/download");
        Assert.That(await download.Content.ReadAsByteArrayAsync(), Is.EqualTo(await File.ReadAllBytesAsync(Path.Combine(folder, "Orbit.zip"))));
        Assert.That(download.Content.Headers.LastModified, Is.EqualTo(new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(folder, "Orbit.zip")))).Within(TimeSpan.FromSeconds(1)));
    }
}
