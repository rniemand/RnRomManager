using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>ZIP Compression Z4: every library write uses the game's system level.</summary>
[Category("Integration")]
public class ZipCompressionWriteTests
{
    private ApiFactory _factory = null!;
    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [OneTimeSetUp]
    public async Task StartHost()
    {
        _factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        _factory.Settings["Sources:ThumbnailBaseUrl"] = "";
        await _factory.StartSharedAsync();
    }

    [OneTimeTearDown]
    public void StopHost() => _factory.Dispose();

    [SetUp]
    public Task SetUp() => _factory.ResetAsync();

    [Test]
    public async Task GameEdit_OnAStoreSystem_WritesEveryEntryStored()
    {
        var bytes = new byte[64 * 1024]; // zeros: Med would deflate them to almost nothing
        var game = TestLibrary.Game("Tetris", TestLibrary.Rom("Tetris (World).gb", bytes));
        const string zipPath = "gb/T/Tetris.zip";
        var zip = Path.GetFullPath(Path.Combine(_factory.Root, "roms", zipPath));
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        await new LibraryZipWriter(Service<InfoJsonReader>()).WriteAsync(zip, null,
            new LibraryZipChanges(game, new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry("Tetris (World).gb")] = () => new MemoryStream(bytes) }));
        var file = new FileInfo(zip);
        await Service<ILibraryMirror>().ReplaceAsync(new MirrorEntry(game, zipPath, file.Length, file.LastWriteTimeUtc), default);
        await Service<IRomStorageSettings>().SetCompressionAsync("gb", ZipCompression.Store, default);

        using var admin = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "boss", TestUsers.AllRoles);
        (await admin.LoginAsync("boss", TestUsers.Password)).Dispose();
        using (var response = await admin.PatchAsync($"/api/games/{game.GameId}", new { description = "x" }))
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var archive = ZipFile.OpenRead(zip);
        Assert.That(archive.Entries.Select(e => e.FullName), Is.SupersetOf(new[] { LibraryZip.InfoJsonEntry, LibraryZip.RomEntry("Tetris (World).gb") }));
        foreach (var entry in archive.Entries)
            Assert.That(entry.CompressedLength, Is.EqualTo(entry.Length), $"{entry.FullName} is stored");
    }
}
