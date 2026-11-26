using RnRomManager.Core.Audit;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Media;

/// <summary>
/// Admin actions R1–R3, "Refresh IGDB Ratings": every IGDB-linked game's <c>total_rating</c> and vote count, read in
/// batches and stored in <c>game_actions</c> only (never a ZIP). One job item per game; cancellable between batches; an
/// IGDB read failure fails the job with its title.
/// </summary>
public sealed class IgdbSyncJob(IGameRepository games, IIgdbGames igdb, IJobItemRepository items, TimeProvider time) : IJob
{
    public const string Updated = "Updated";
    public const string NoRating = "NoRating";
    public const string NotFound = "NotFound";

    public JobType Type => JobType.IgdbSync;
    public JobMode? Mode => null;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var linked = await games.ListIgdbLinkedAsync(cancellationToken).ConfigureAwait(false);
        context.Progress.SetTotal(linked.Count);
        int updated = 0, noRating = 0, notFound = 0;

        foreach (var batch in linked.Chunk(IgdbClient.MaxBatch))
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            var found = (await igdb.GetGamesAsync(batch.Select(g => g.IgdbId!.Value).Distinct().ToList(), cancellationToken).ConfigureAwait(false))
                .ToDictionary(g => g.Id);
            // A batch is finished once read, so the database matches what IGDB returned for all of it.
            foreach (var row in batch)
            {
                context.Progress.SetCurrentItem(row.ZipPath);
                var rating = found.GetValueOrDefault(row.IgdbId!.Value);
                var outcome = rating is null ? NotFound : rating.TotalRating is null ? NoRating : Updated;
                var now = time.GetUtcNow().UtcDateTime;
                await games.SaveIgdbSyncAsync(row.Id, rating?.TotalRating, rating?.TotalRatingCount, now, CancellationToken.None).ConfigureAwait(false);
                if (outcome == Updated)
                    updated++;
                else if (outcome == NoRating)
                    noRating++;
                else
                    notFound++;
                await items.InsertAsync(new JobItem
                {
                    JobId = context.JobId,
                    FileId = row.Id,
                    GameId = row.Id,
                    SystemId = row.SystemId,
                    SourcePath = AuditPaths.Library(row.ZipPath),
                    Outcome = outcome,
                    CreatedAt = now,
                }, CancellationToken.None).ConfigureAwait(false);
                context.Progress.ItemDone();
            }
        }
        return new { updated, noRating, notFound };
    }
}
