using System.Net;
using Dapper;
using RnRomManager.Core.Database;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Users;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec G4 and I4 for metadata-only edits and media retry: the ZIP is opened checked and must be the mirrored game.</summary>
public partial class ExternalLibraryTests
{
    /// <summary>A 64×32 PNG header: enough for the image checks.</summary>
    private static readonly byte[] SnapPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0, 64, 0, 0, 0, 32,
        8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "IEND"u8, 0xAE, 0x42, 0x60, 0x82];

    private async Task<long> RomIdAsync(Guid gameId)
    {
        await using var db = Service<IDbConnectionFactory>().Create();
        return await db.QueryFirstAsync<long>("SELECT id FROM roms WHERE game_id = @id", new { id = gameId.ToString() });
    }

    /// <summary>Moves <paramref name="zip"/> outside the library and puts a link to it in its place; false when links can't be made.</summary>
    private bool LinkOutside(string zip, out string outside)
    {
        outside = Path.Combine(Directory.CreateDirectory(Path.Combine(_factory.Root, "outside")).FullName, Path.GetFileName(zip));
        File.Move(zip, outside);
        return TestLinks.TryLinkFile(zip, outside);
    }

    [Test]
    public async Task LinkedMetadataZip_CannotBeEdited_OrRetried()
    {
        var gameId = await FirstReleaseAsync();
        var romId = await RomIdAsync(gameId);
        if (!LinkOutside(ZipPath, out var outside))
            Assert.Ignore("this account can't create a file link");
        var before = File.ReadAllBytes(outside);
        var editor = EditorWith();

        Assert.That((await editor.PatchAsync(gameId, new GamePatch { Favourite = true }, null, default)).Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That((await editor.SaveNoteAsync(gameId, romId, "hi", null, default)).Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That((await editor.AddImageAsync(gameId, ImageType.Snap, SnapPng, Editor, default)).Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That((await editor.RetryMediaAsync(gameId, Editor, default)).Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That(File.ReadAllBytes(outside), Is.EqualTo(before));
        Assert.That(File.Exists(outside + ".tmp") || File.Exists(ZipPath + ".tmp"), Is.False);
    }

    [Test]
    public async Task MetadataOnlyWrites_WithAStaleIdentity_AreRejected()
    {
        var gameId = await FirstReleaseAsync();
        var romId = await RomIdAsync(gameId);
        var (otherId, _) = await QueueAsync("mystery.xci");
        Assert.That((await Resolver().ResolveInJobAsync(otherId, ManualMystery, await AdminAsync(), default)).Code, Is.Null);
        await SwapZipPathsAsync(ZipRelative, "ext/M/Mystery Game.zip");
        var mystery = Path.Combine(_factory.Root, "roms", "ext", "M", "Mystery Game.zip");
        var before = (File.ReadAllBytes(ZipPath), File.ReadAllBytes(mystery));
        var editor = EditorWith();

        Assert.That((await editor.PatchAsync(gameId, new GamePatch { SetRating = true, Rating = 4 }, null, default)).Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That((await editor.SaveNoteAsync(gameId, romId, "hi", null, default)).Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That((await editor.AddImageAsync(gameId, ImageType.Snap, SnapPng, Editor, default)).Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That((await editor.RetryMediaAsync(gameId, Editor, default)).Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That((File.ReadAllBytes(ZipPath), File.ReadAllBytes(mystery)), Is.EqualTo(before));
    }

    [Test]
    public async Task EmbeddedGame_MirroredUnderAnExternalFolder_IsChecked()
    {
        var gb = Path.Combine(_factory.Root, "ingest", "gb", "shared.gb");
        File.WriteAllBytes(gb, SharedGb);
        var embedded = await IngestAsync(Pipeline(), new IngestFile("gb", gb, "shared.gb", SharedGb.Length, DateTime.UtcNow.AddMinutes(-1)));
        Assert.That(Code(embedded), Is.EqualTo((IngestOutcome.Ingested, (string?)null)));
        var gameId = embedded!.GameId!.Value;
        var romId = await RomIdAsync(gameId);
        var zip = Path.Combine(Directory.CreateDirectory(Path.Combine(_factory.Root, "roms", "ext", "G")).FullName, "Shared.zip");
        File.Move(Path.Combine(_factory.Root, "roms", "gb", "S", "Shared.zip"), zip);
        await using (var db = Service<IDbConnectionFactory>().Create())
            await db.ExecuteAsync("UPDATE games SET zip_path = 'ext/G/Shared.zip' WHERE id = @id", new { id = gameId.ToString() });
        var editor = EditorWith();

        // A regular ZIP there is still written, through the checked write.
        Assert.That((await editor.SaveNoteAsync(gameId, romId, "kept", null, default)).Code, Is.Null);
        using (var library = LibraryZip.Open(zip, Service<InfoJsonReader>()))
            Assert.That(library.HasEntry(LibraryZip.NoteEntry("Shared (World).gb")), Is.True);

        // The successful write rebuilt the mirror and assigned a new ROM row ID.
        romId = await RomIdAsync(gameId);
        if (!LinkOutside(zip, out var outside))
            Assert.Ignore("this account can't create a file link");
        var before = File.ReadAllBytes(outside);

        Assert.That((await editor.SaveNoteAsync(gameId, romId, "changed", null, default)).Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That((await editor.PatchAsync(gameId, new GamePatch { Favourite = true }, null, default)).Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That((await editor.RetryMediaAsync(gameId, Editor, default)).Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That(File.ReadAllBytes(outside), Is.EqualTo(before));
    }

    [Test]
    public async Task PlainUser_CanRateAndNote_AnExternalGameWithAMissingFile()
    {
        var gameId = await FirstReleaseAsync();
        File.Delete(Sidecar);
        using var client = await AuthClient.CreateAsync(_factory);
        await TestUsers.CreateAsync(_factory, "someone", Roles.Of());
        (await client.LoginAsync("someone", TestUsers.Password)).Dispose();

        using (var rated = await client.PatchAsync($"/api/games/{gameId}/rating", new { rating = 4, favourite = true }))
            Assert.That(rated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using (var saved = await client.SendAsync(HttpMethod.Put, $"/api/games/{gameId}/roms/{await RomIdAsync(gameId)}/note", new { text = "great" }, true))
            Assert.That(saved.IsSuccessStatusCode, Is.True, saved.StatusCode.ToString());
        Assert.That(ReadGame(), Has.Property(nameof(InfoJsonGame.Rating)).EqualTo(4).And.Property(nameof(InfoJsonGame.Favourite)).True);
        using (var zip = LibraryZip.Open(ZipPath, Service<InfoJsonReader>()))
            Assert.That(zip.HasEntry(LibraryZip.NoteEntry(ReadGame().Roms[0].FileName)), Is.True);

        using (var deleted = await client.SendAsync(HttpMethod.Delete, $"/api/games/{gameId}/roms/{await RomIdAsync(gameId)}/note", null, true))
            Assert.That(deleted.IsSuccessStatusCode, Is.True, deleted.StatusCode.ToString());
        using (var zip = LibraryZip.Open(ZipPath, Service<InfoJsonReader>()))
            Assert.That(zip.HasEntry(LibraryZip.NoteEntry(ReadGame().Roms[0].FileName)), Is.False);
    }

    [Test]
    public async Task ValidManagementWrites_StillSucceed()
    {
        var gameId = await FirstReleaseAsync();
        var editor = EditorWith();

        Assert.That((await editor.PatchAsync(gameId, new GamePatch { SetReleaseYear = true, ReleaseYear = 2020 }, Editor, default)).Code, Is.Null);
        Assert.That((await editor.AddImageAsync(gameId, ImageType.Snap, SnapPng, Editor, default)).Code, Is.Null);
        Assert.That((await editor.RetryMediaAsync(gameId, Editor, default)).Code, Is.Null);
        Assert.That(ReadGame().ReleaseYear, Is.EqualTo(2020));
        Assert.That(ReadGame().Images.Any(i => i.Type == ImageType.Snap), Is.True);
        Assert.That(File.ReadAllBytes(Sidecar), Is.EqualTo(Rom));
    }
}
