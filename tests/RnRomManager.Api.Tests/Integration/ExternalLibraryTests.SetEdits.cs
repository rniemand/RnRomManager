using System.Net;
using Dapper;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Disc systems B16/B17: title edits and deletes of a game whose release is a bin/cue set.</summary>
public partial class ExternalLibraryTests
{
    private static readonly string[] SetFiles = [SetCue, SetTrack1, SetTrack2];

    /// <summary>"Set Game" stored from its cue, data track and audio track, in <c>roms/extset/S/</c>.</summary>
    private async Task<Guid> SetGameAsync()
    {
        DropSetGame();
        await RunJobAsync(Pipeline());
        var gameId = _items.First(i => i.GameId is not null).GameId!.Value;
        _items.Clear();
        return gameId;
    }

    private string[] FilesIn(char bucket) =>
        Directory.Exists(SetFolder(bucket)) ? [.. Directory.GetFiles(SetFolder(bucket)).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)] : [];

    private static string[] With(params string[] names) => [.. SetFiles.Concat(names).Order(StringComparer.Ordinal)];

    [Test]
    public async Task SetTitleEdit_InTheSameLetterFolder_RenamesOnlyTheZip()
    {
        var gameId = await SetGameAsync();

        var result = await RetitleAsync(gameId, "Set Game Deluxe");

        Assert.That(result.Code, Is.Null, result.Details);
        Assert.That(FilesIn('S'), Is.EqualTo(With("Set Game Deluxe.zip")));
        Assert.That(Read(Path.Combine(SetFolder('S'), "Set Game Deluxe.zip")).ExternalFileNames(), Is.EqualTo(new[] { SetTrack1, SetCue, SetTrack2 }));
    }

    [Test]
    public async Task SetTitleEdit_WhenAnotherGameClaimsASetFile_IsRefused()
    {
        var gameId = await SetGameAsync();
        await IngestAsync(Pipeline(), DropSet("single.bin", SingleBin)); // Single Game, also in S/
        await SqlAsync("UPDATE roms SET external_file_name = @cue WHERE base_title = 'Single Game'", new { cue = SetCue });

        var result = await RetitleAsync(gameId, "Set Game Deluxe");

        Assert.That(result.Code, Is.EqualTo(LibraryTarget.TargetZipUnreadable));
        Assert.That(result.Details, Does.Contain(SetCue));
    }

    [Test]
    public async Task SetTitleEdit_ToAnotherLetterFolder_MovesEveryFile_AndATakenNameIs409WithNothingMoved()
    {
        var gameId = await SetGameAsync();
        Directory.CreateDirectory(SetFolder('Z'));
        File.WriteAllBytes(Path.Combine(SetFolder('Z'), SetTrack2), [1]);
        using var admin = await AdminClientAsync();

        using (var refused = await admin.PatchAsync($"/api/games/{gameId}", new { title = "Zeta Game" }))
        {
            Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(await AuthClient.CodeAsync(refused), Is.EqualTo(LibraryEditResult.NameTaken));
        }
        Assert.That(FilesIn('S'), Is.EqualTo(With("Set Game.zip")));
        Assert.That(FilesIn('Z'), Is.EqualTo(new[] { SetTrack2 }));

        File.Delete(Path.Combine(SetFolder('Z'), SetTrack2));
        var result = await RetitleAsync(gameId, "Zeta Game");

        Assert.That(result.Code, Is.Null, result.Details);
        Assert.That(FilesIn('S'), Is.Empty);
        Assert.That(FilesIn('Z'), Is.EqualTo(With("Zeta Game.zip")));
        Assert.That(File.ReadAllBytes(Path.Combine(SetFolder('Z'), SetTrack2)), Is.EqualTo(Audio));
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QueryAsync<string>("SELECT external_file_name FROM rom_files ORDER BY ordinal"), Is.EqualTo(new[] { SetCue, SetTrack2 }));
    }

    [Test]
    public async Task SetReleaseDelete_MovesEveryFileToRemoved_WithOneAuditEvent()
    {
        var gameId = await SetGameAsync();
        DropDisc("Set Game (USA) (Disc 2).cue", ("Set Game (USA) (Disc 2) (Track 1).bin", SetData2), ("Set Game (USA) (Disc 2) (Track 2).bin", Audio2));
        await RunJobAsync(Pipeline());

        var result = await EditorWith().DeleteReleaseAsync(gameId, await RomIdAsync(SetData), Editor, default);

        Assert.That(result.Code, Is.Null, result.Details);
        var removed = Path.Combine(SetIngest, StorageFolders.RemovedFolder);
        Assert.That(Directory.GetFiles(removed).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal), Is.EqualTo(SetFiles.Order(StringComparer.Ordinal)));
        var deleted = (await AuditAsync()).Where(a => a.Action == AuditActions.ReleaseDeleted).ToList();
        Assert.That(deleted, Is.EqualTo(new[] { (AuditActions.ReleaseDeleted, (string?)$"{SetTrack1}; {SetCue}; {SetTrack2}") }));
        Assert.That(FilesIn('S'), Has.None.StartsWith("Set Game (USA) (Track").And.None.EqualTo(SetCue));
    }

    [Test]
    public async Task SetGameDelete_MovesEveryFile_WithOneGameDeletedEvent()
    {
        var gameId = await SetGameAsync();

        var result = await EditorWith().DeleteGameAsync(gameId, Editor, default);

        Assert.That(result.Code, Is.Null, result.Details);
        Assert.That(Directory.GetFiles(Path.Combine(SetIngest, StorageFolders.RemovedFolder)).Length, Is.EqualTo(3));
        Assert.That(FilesIn('S'), Is.Empty);
        Assert.That((await AuditAsync()).Count(a => a.Action == AuditActions.GameDeleted), Is.EqualTo(1));
        await using var db = Service<IDbConnectionFactory>().Create();
        Assert.That(await db.QuerySingleAsync<long>("SELECT COUNT(*) FROM rom_files"), Is.Zero);
    }
}
