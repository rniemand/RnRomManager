using System.IO.Compression;
using Dapper;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>User Management S6: per-owner save counts written by the mirror from <c>saves/saves.json</c>.</summary>
public partial class LibraryMirrorTests
{
    private ILibraryMirror CountingMirror() => new LibraryMirror(_factory, Options.Create(new PathsOptions { Roms = _dir.Path }));

    /// <summary>A version 1 record (release editing B16): only that version holds unowned or invalid owners.</summary>
    private static GameSave Save(int n, string? owner) => new()
    {
        Legacy = true,
        Id = n.ToString("x32"), Name = $"Save {n}", Description = "", Kind = GameSaves.KindBattery, Release = "a.gb", Extension = ".srm",
        SizeBytes = 1, Sha1 = new string('0', 40), UploadedBy = owner, UploadedAt = DateTimeOffset.UnixEpoch, UpdatedBy = owner,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <param name="index">The saves.json bytes, or null for none; every save gets a one-byte entry.</param>
    private void WriteSavesZip(IReadOnlyList<GameSave> saves, byte[]? index)
    {
        var path = Abs("gb/T/Tetris.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("info.json");
        if (index is not null)
            using (var stream = zip.CreateEntry(LibraryZip.SavesIndexEntry).Open())
                stream.Write(index);
        foreach (var save in saves)
            using (var stream = zip.CreateEntry(save.Entry).Open())
                stream.WriteByte(1);
    }

    private async Task<Dictionary<string, int>> OwnerCountsAsync(Guid gameId)
    {
        await using var db = _factory.Create();
        return (await db.QueryAsync<(string Username, int Count)>(
                "SELECT username, save_count FROM game_user_save_counts WHERE game_id = @gameId", new { gameId = gameId.ToString() }))
            .ToDictionary(r => r.Username, r => r.Count);
    }

    [Test]
    public async Task SaveCounts_OnePerOwner_GroupedIgnoringCase_UnownedAndInvalidOwnersOnlyInTheTotal()
    {
        var saves = new[] { Save(1, "Bob"), Save(2, "bob"), Save(3, "alice"), Save(4, null), Save(5, "x"), Save(6, "has space") };
        WriteSavesZip(saves, TestSaves.V1Index([.. saves]));
        var game = Game("Tetris", Rom("a.gb", RomBytes(1)));

        await CountingMirror().ReplaceAsync(Entry(game), CancellationToken.None);

        Assert.That((await _games.GetAsync(game.GameId, CancellationToken.None))!.SaveCount, Is.EqualTo(6));
        var counts = await OwnerCountsAsync(game.GameId);
        Assert.That(counts, Has.Count.EqualTo(2));
        Assert.That(counts.Single(c => c.Key.Equals("bob", StringComparison.OrdinalIgnoreCase)).Value, Is.EqualTo(2));
        Assert.That(counts["alice"], Is.EqualTo(1));
    }

    [Test]
    public async Task SaveCounts_UnreadableOrMissingIndex_WritesNoRows_TotalUnchanged()
    {
        var saves = new[] { Save(1, "bob"), Save(2, "bob") };
        var game = Game("Tetris", Rom("a.gb", RomBytes(1)));

        WriteSavesZip(saves, "{ not json"u8.ToArray());
        await CountingMirror().ReplaceAsync(Entry(game), CancellationToken.None);
        Assert.That((await _games.GetAsync(game.GameId, CancellationToken.None))!.SaveCount, Is.EqualTo(2));
        Assert.That(await OwnerCountsAsync(game.GameId), Is.Empty);

        WriteSavesZip(saves, null);
        await CountingMirror().ReplaceAsync(Entry(game), CancellationToken.None);
        Assert.That(await OwnerCountsAsync(game.GameId), Is.Empty);

        WriteSavesZip(saves, new byte[GameSaves.MaxIndexBytes + 1]);
        await CountingMirror().ReplaceAsync(Entry(game), CancellationToken.None);
        Assert.That(await OwnerCountsAsync(game.GameId), Is.Empty);
    }

    [Test]
    public async Task SaveCounts_AreReplacedOnTheNextWrite_AndCascadeWithTheGame()
    {
        var mirror = CountingMirror();
        var game = Game("Tetris", Rom("a.gb", RomBytes(1)));
        var first = new[] { Save(1, "bob"), Save(2, "bob") };
        WriteSavesZip(first, TestSaves.V1Index([.. first]));
        await mirror.ReplaceAsync(Entry(game), CancellationToken.None);

        var second = new[] { Save(3, "alice") };
        WriteSavesZip(second, TestSaves.V1Index([.. second]));
        await mirror.ReplaceAsync(Entry(game), CancellationToken.None);
        Assert.That(await OwnerCountsAsync(game.GameId), Is.EqualTo(new Dictionary<string, int> { ["alice"] = 1 }));

        await mirror.DeleteAsync(game.GameId, CancellationToken.None);
        Assert.That(await OwnerCountsAsync(game.GameId), Is.Empty);
    }

    [Test]
    public async Task GetUserSaveCounts_OwnCountAfterAMirrorWrite_MissingIsAbsent()
    {
        var saves = new[] { Save(1, "bob"), Save(2, "bob") };
        WriteSavesZip(saves, TestSaves.V1Index([.. saves]));
        var game = Game("Tetris", Rom("a.gb", RomBytes(1)));
        await CountingMirror().ReplaceAsync(Entry(game), CancellationToken.None);
        var other = Guid.NewGuid();

        var bob = await _games.GetUserSaveCountsAsync("BOB", [game.GameId, other], CancellationToken.None);
        var alice = await _games.GetUserSaveCountsAsync("alice", [game.GameId], CancellationToken.None);

        Assert.That(bob, Is.EqualTo(new Dictionary<Guid, int> { [game.GameId] = 2 }));
        Assert.That(alice, Is.Empty);
        Assert.That(await _games.GetUserSaveCountsAsync("bob", [], CancellationToken.None), Is.Empty);
    }
}
