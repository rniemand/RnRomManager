using Dapper;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using SkiaSharp;
using static RnRomManager.Core.Integrity.IntegrityIssueType;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>A5 (the actions check and orphan sweep) and C5 (the image cache step).</summary>
public partial class IntegrityJobTests
{
    private static byte[] RealPng()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Teal);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string Sha1Of(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes));

    private async Task Sql(string sql, object? args = null)
    {
        await using var connection = _factory.Create();
        await connection.ExecuteAsync(sql, args);
    }

    private string CacheFile(params string[] relative)
    {
        var path = _cache.Combine(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    [Test]
    public async Task Deep_CoverPulledMismatch_IsRemirrored_AsDbRowUpdated()
    {
        var game = await Write("gb/T/Tetris.zip", "Tetris", edit: g => g with { Actions = new InfoJsonActions { CoverImagePulled = InfoJsonActions.Format(When) } });
        await Run();
        Assert.That((await _games.ListCoverActionsAsync(default)).Keys, Is.EqualTo(new[] { game.GameId }), "DB repair mirrors actions");
        await Sql("UPDATE game_actions SET cover_image_pulled_at = NULL");

        await Run(JobMode.Quick);
        Assert.That(await _games.ListCoverActionsAsync(default), Is.Empty, "Quick doesn't open an unchanged ZIP");
        var (jobId, _) = await Run(JobMode.Deep);

        Assert.That(await _games.ListCoverActionsAsync(default),
            Is.EqualTo(new Dictionary<Guid, (DateTime?, DateTime?)> { [game.GameId] = (When.UtcDateTime, null) }));
        Assert.That((await All()).Where(i => i.LastSeenJobId == jobId).Select(i => (i.Type, i.Status, i.GameId)),
            Is.EqualTo(new[] { (DbRowUpdated, IntegrityIssue.AutoRepaired, (Guid?)game.GameId) }));
    }

    [Test]
    public async Task Game_CoverPulledMismatch_IsRemirrored()
    {
        var game = await Write("gb/T/Tetris.zip", "Tetris");
        await Run();
        await Sql("INSERT INTO game_actions (game_id, cover_image_pulled_at) VALUES (@id, '2026-02-03 04:05:06')", new { id = game.GameId });

        await RunGame("gb/T/Tetris.zip");

        Assert.That(await _games.ListCoverActionsAsync(default), Is.Empty);
        Assert.That((await All()).Count(i => i.Type == DbRowUpdated), Is.EqualTo(1));
    }

    [Test]
    public async Task Deep_CoverCheckedMismatch_IsRemirrored_AsDbRowUpdated()
    {
        var game = await Write("gb/T/Tetris.zip", "Tetris", edit: g => g with { Actions = new InfoJsonActions { CoverImageChecked = InfoJsonActions.Format(When) } });
        await Run();
        await Sql("UPDATE game_actions SET cover_image_checked_at = '2026-02-03 04:05:06'");

        var (jobId, _) = await Run(JobMode.Deep);

        Assert.That(await _games.ListCoverActionsAsync(default),
            Is.EqualTo(new Dictionary<Guid, (DateTime?, DateTime?)> { [game.GameId] = (null, When.UtcDateTime) }));
        Assert.That((await All()).Where(i => i.LastSeenJobId == jobId).Select(i => (i.Type, i.Status, i.GameId)),
            Is.EqualTo(new[] { (DbRowUpdated, IntegrityIssue.AutoRepaired, (Guid?)game.GameId) }));
    }

    [Test]
    public async Task Quick_DeletesActionRowsOfGamesThatAreGone()
    {
        var game = await Write("gb/T/Tetris.zip", "Tetris");
        await Run();
        await Sql("INSERT INTO game_actions (game_id, igdb_rating) VALUES (@id, 80), (@gone, 70)", new { id = game.GameId, gone = Guid.NewGuid() });

        await Run();

        await using var connection = _factory.Create();
        Assert.That(await connection.QueryAsync<Guid>("SELECT game_id FROM game_actions"), Is.EqualTo(new[] { game.GameId }));
    }

    [Test]
    public async Task Quick_OneStaleCacheFile_IsRemoved()
    {
        var png = RealPng();
        var game = await Write("gb/T/Tetris.zip", "Tetris", image: png);
        await Run();
        var stale = CacheFile(game.GameId.ToString("D"), new string('0', 40) + ".webp");

        var (_, summary) = await Run();

        Assert.That(File.Exists(stale), Is.False);
        Assert.That(summary, Does.Contain("\"cacheGenerated\":0,\"cacheRemoved\":1,\"cacheFailed\":0"));
    }

    [Test]
    public async Task Quick_GeneratesMissingCovers_SweepsEverythingElse_KeepsTheProbeAndFreshTmp()
    {
        var png = RealPng();
        var game = await Write("gb/T/Tetris.zip", "Tetris", image: png);
        await Write("gb/D/Dr Mario.zip", "Dr Mario"); // an undecodable cover: counted, never an issue
        var folder = game.GameId.ToString("D");
        var oldTmp = CacheFile(folder, "x.webp.1.tmp");
        File.SetLastWriteTimeUtc(oldTmp, DateTime.UtcNow - IntegrityJob.TmpGrace - TimeSpan.FromMinutes(1));
        var freshTmp = CacheFile(folder, "y.webp.2.tmp");
        var probe = CacheFile(StorageFolders.ProbeFileName);
        var otherGame = CacheFile(Guid.NewGuid().ToString("D"), "a.webp");
        var junk = CacheFile("junk.txt");

        var (_, summary) = await Run();

        var cached = _cache.Combine(folder, Sha1Of(png) + ".webp");
        using (var codec = SKCodec.Create(cached))
            Assert.That(codec.EncodedFormat, Is.EqualTo(SKEncodedImageFormat.Webp));
        Assert.That(new[] { oldTmp, otherGame, junk }.Where(File.Exists), Is.Empty);
        Assert.That(new[] { freshTmp, probe }.All(File.Exists), Is.True);
        Assert.That(Directory.Exists(Path.GetDirectoryName(otherGame)), Is.False);
        Assert.That(summary, Does.Contain("\"cacheGenerated\":1,\"cacheRemoved\":3,\"cacheFailed\":1"));
        Assert.That((await Open()).Select(o => o.Type), Has.No.Member(ZipUnreadable));
    }

    [Test]
    public async Task CancelledRun_RemovesNothingFromTheCache()
    {
        await Write("gb/T/Tetris.zip", "Tetris", image: RealPng());
        var junk = CacheFile("junk.txt");
        using var cancel = new CancellationTokenSource();
        _onScan = cancel.Cancel;

        var (_, summary) = await Run(ct: cancel.Token);

        Assert.That(File.Exists(junk), Is.True);
        Assert.That(summary, Does.Contain("\"cacheRemoved\":0"));
    }

    [Test]
    public async Task Game_SkipsTheCacheStep()
    {
        var game = await Write("gb/T/Tetris.zip", "Tetris", image: RealPng());
        await Run();
        var stale = CacheFile(game.GameId.ToString("D"), new string('0', 40) + ".webp");
        File.Delete(_cache.Combine(game.GameId.ToString("D"), Sha1Of(RealPng()) + ".webp"));

        var summary = await RunGame("gb/T/Tetris.zip");

        Assert.That(File.Exists(stale), Is.True);
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(stale)!), Has.Length.EqualTo(1));
        Assert.That(summary, Does.Not.Contain("cache"));
    }
}
