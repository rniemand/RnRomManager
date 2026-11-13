using Microsoft.Extensions.Logging;
using RnRomManager.Core.Jobs;

namespace RnRomManager.Core.Ingest;

/// <summary>
/// B1–B5: one Review resolution as a background job. The endpoint sets <see cref="ReviewId"/>, <see cref="FileName"/>
/// and <see cref="Request"/> before <c>StartAsync</c> (the job is transient, one per request). Not cancellable: a
/// resolution runs to its end. The summary carries the outcome; one <c>job_items</c> row per review row acted on.
/// </summary>
public sealed class ReviewResolveJob(ReviewResolver resolver, IJobItemRepository items, TimeProvider time, ILogger<ReviewResolveJob> logger) : IJob
{
    public long ReviewId { get; set; }

    /// <summary>The item's <c>originalFileName</c> as the endpoint read it, so the summary names it even when the item is gone.</summary>
    public string FileName { get; set; } = "";

    public ReviewResolutionRequest Request { get; set; } = new();

    public JobType Type => JobType.ReviewResolve;
    public JobMode? Mode => null;
    public bool Cancellable => false;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var result = await resolver.ResolveInJobAsync(ReviewId, Request, context.Actor, context.Progress).ConfigureAwait(false);
        foreach (var row in result.Items)
        {
            try
            {
                await items.InsertAsync(new JobItem
                {
                    JobId = context.JobId,
                    FileId = Guid.NewGuid(),
                    SystemId = row.SystemId,
                    SourcePath = row.ArchivePath ?? row.SourcePath,
                    ArchiveEntry = row.ArchiveEntry,
                    Outcome = result.Code is null ? IngestOutcome.Ingested : IngestOutcome.Error,
                    Reason = result.Code,
                    GameId = result.Code is null ? result.GameId : null,
                    Sha1 = row.Sha1,
                    CreatedAt = time.GetUtcNow().UtcDateTime,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // B5: unlike ingest, the summary the SPA reads is kept.
                logger.LogWarning(ex, "Review {ReviewId}: recording job item for {Path} failed", ReviewId, row.SourcePath);
            }
        }
        if (result.Code is null)
            logger.LogInformation("Review {ReviewId} ({FileName}) resolved into game {GameId}", ReviewId, FileName, result.GameId);
        else
            logger.LogWarning("Review {ReviewId} ({FileName}) not resolved: {Code}", ReviewId, FileName, result.Code);
        return new
        {
            reviewId = ReviewId, fileName = FileName, gameId = result.Code is null ? result.GameId : null, code = result.Code, errors = result.Errors,
        };
    }
}
