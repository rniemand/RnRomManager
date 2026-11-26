using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Core.Tests.Media;

/// <summary>Admin actions R2/R3: the <c>IgdbSync</c> job with IGDB and the database substituted. It has no ZIP to touch.</summary>
public class IgdbSyncJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private IGameRepository _games = null!;
    private IIgdbGames _igdb = null!;
    private IJobItemRepository _items = null!;
    private List<JobItem> _written = null!;

    [SetUp]
    public void SetUp()
    {
        _games = Substitute.For<IGameRepository>();
        _igdb = Substitute.For<IIgdbGames>();
        _items = Substitute.For<IJobItemRepository>();
        _written = [];
        _items.InsertAsync(Arg.Do<JobItem>(_written.Add), Arg.Any<CancellationToken>()).Returns(1L);
    }

    private static GameRow Row(string title, int igdbId) =>
        new MirrorEntry(Game(title, Rom($"{title}.gb", RomBytes(igdbId))) with { IgdbId = igdbId }, $"gb/{title[0]}/{title}.zip", 1, DateTime.UtcNow)
            .ToRows().Game;

    private async Task<string> Run(CancellationToken ct, params GameRow[] linked)
    {
        _games.ListIgdbLinkedAsync(Arg.Any<CancellationToken>()).Returns(linked);
        var job = new IgdbSyncJob(_games, _igdb, _items, new FakeTimeProvider(Now));
        return JsonSerializer.Serialize(await job.RunAsync(new JobContext(Guid.NewGuid(), new JobProgress()), ct));
    }

    [Test]
    public async Task EachOutcome_IsSaved_AndItemized()
    {
        var (rated, unrated, missing) = (Row("Rated", 1), Row("Unrated", 2), Row("Missing", 3));
        _igdb.GetGamesAsync(default!, default).ReturnsForAnyArgs([new IgdbGameRating(1, null, 81.6, 12), new IgdbGameRating(2, "c", null, null)]);

        var summary = await Run(CancellationToken.None, rated, unrated, missing);

        Assert.That(summary, Is.EqualTo("""{"updated":1,"noRating":1,"notFound":1}"""));
        await _games.Received(1).SaveIgdbSyncAsync(rated.Id, 81.6, 12, Now.UtcDateTime, Arg.Any<CancellationToken>());
        await _games.Received(1).SaveIgdbSyncAsync(unrated.Id, null, null, Now.UtcDateTime, Arg.Any<CancellationToken>());
        await _games.Received(1).SaveIgdbSyncAsync(missing.Id, null, null, Now.UtcDateTime, Arg.Any<CancellationToken>());
        Assert.That(_written.Select(i => (i.GameId, i.FileId, i.SourcePath, i.Outcome)), Is.EqualTo(new[]
        {
            ((Guid?)rated.Id, rated.Id, "roms/gb/R/Rated.zip", "Updated"),
            ((Guid?)unrated.Id, unrated.Id, "roms/gb/U/Unrated.zip", "NoRating"),
            ((Guid?)missing.Id, missing.Id, "roms/gb/M/Missing.zip", "NotFound"),
        }));
    }

    [Test]
    public async Task Batches_Of500()
    {
        var rows = Enumerable.Range(1, 501).Select(i => Row($"G{i}", i)).ToArray();
        _igdb.GetGamesAsync(default!, default).ReturnsForAnyArgs([]);

        await Run(CancellationToken.None, rows);

        await _igdb.Received(1).GetGamesAsync(Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 500), Arg.Any<CancellationToken>());
        await _igdb.Received(1).GetGamesAsync(Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 501 })), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IgdbFailure_FailsTheJob_WithTheTitle_SavingNothing()
    {
        _igdb.GetGamesAsync(default!, default).ReturnsForAnyArgs(Task.FromException<IReadOnlyList<IgdbGameRating>>(new InvalidOperationException(IgdbClient.Unreachable)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(CancellationToken.None, Row("A", 1)));

        Assert.That(ex!.Message, Is.EqualTo(IgdbClient.Unreachable));
        await _games.DidNotReceiveWithAnyArgs().SaveIgdbSyncAsync(default, default, default, default, default);
    }

    [Test]
    public async Task Cancelled_StopsBeforeTheNextBatch()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.That(await Run(cancelled.Token, Row("A", 1)), Is.EqualTo("""{"updated":0,"noRating":0,"notFound":0}"""));
        await _igdb.DidNotReceiveWithAnyArgs().GetGamesAsync(default!, default);
    }
}
