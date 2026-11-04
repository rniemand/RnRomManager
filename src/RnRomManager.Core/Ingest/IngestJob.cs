using Microsoft.Extensions.Logging;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>
/// The <c>Ingest</c> job (spec §7.1): refuses with <c>DatsMissing</c> before any row exists, then the DB sync pre-pass,
/// stale <c>Pending</c> review rows, and the snapshot (files younger than 10 s are <c>Skipped</c> <c>TooRecent</c>).
/// Each other file goes through <see cref="IngestPipeline"/>; one <c>job_items</c> row per item. An item's unexpected
/// exception is <c>Error</c> <c>Unexpected</c> and the next file continues; <c>DbWriteFailed</c> stops the job as <c>Failed</c>.
/// </summary>
public sealed class IngestJob(
    DatCache dats,
    ISystemRegistry systems,
    DbSync dbSync,
    IIngestReviewRepository reviews,
    IJobItemRepository items,
    IngestFileScanner scanner,
    IngestPipeline pipeline,
    TimeProvider time,
    ILogger<IngestJob> logger) : IJob
{
    public const string DatsMissing = "DatsMissing";
    public const string SyncingLibrary = "Syncing library";
    public static readonly TimeSpan MinFileAge = TimeSpan.FromSeconds(10);

    public JobType Type => JobType.Ingest;
    public JobMode? Mode => null;

    /// <summary>§7.1 step 2: every registered system needs loaded DATs.</summary>
    public string? Preflight() => systems.All.All(h => dats.For(h.Id) is not null) ? null : DatsMissing;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        // Step 4
        context.Progress.SetCurrentItem(SyncingLibrary);
        await dbSync.SyncAllAsync(cancellationToken).ConfigureAwait(false);

        // Step 5
        var stale = (await reviews.ListPendingSourcesAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => !File.Exists(r.ArchivePath ?? r.SourcePath)).ToList();
        if (stale.Count > 0)
        {
            await reviews.DeleteAsync(stale.Select(r => r.Id).ToList(), cancellationToken).ConfigureAwait(false);
            foreach (var row in stale)
                logger.LogInformation("Review item {Id} removed: {Path} no longer exists", row.Id, row.ArchivePath ?? row.SourcePath);
        }

        // Step 6
        var snapshot = scanner.Scan().SelectMany(s => s.Files).ToList();
        context.Progress.SetTotal(snapshot.Count);
        var counts = IngestOutcome.All.ToDictionary(o => o, _ => 0);
        var now = time.GetUtcNow().UtcDateTime;
        var breaker = new CircuitBreaker(); // one per job (§10.2)
        foreach (var file in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Progress.SetCurrentItem(file.Name);
            var fileId = Guid.NewGuid();
            using (logger.BeginScope(new Dictionary<string, object> { ["FileId"] = fileId }))
            {
                var result = now - file.ModifiedAtUtc < MinFileAge
                    ? new IngestItemResult(IngestOutcome.Skipped, IngestReason.TooRecent, SystemId: file.SystemId)
                    : await ProcessAsync(file, context.JobId, breaker, cancellationToken).ConfigureAwait(false);
                if (result is not null)
                {
                    await RecordAsync(context, file, fileId, result, cancellationToken).ConfigureAwait(false);
                    counts[result.Outcome]++;
                    if (result.Reason == IngestErrors.DbWriteFailed)
                        throw new IngestStoppedException($"{file.Name}: DB write failed ({result.Details}); the job stopped so no later file groups against a stale DB");
                }
            }
            context.Progress.ItemDone();
        }

        return counts;
    }

    private async Task<IngestItemResult?> ProcessAsync(IngestFile file, Guid jobId, CircuitBreaker breaker, CancellationToken cancellationToken)
    {
        try
        {
            return await pipeline.ProcessAsync(file, jobId, breaker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "{Path}: unexpected failure", file.FullPath);
            return IngestItemResult.Error(IngestErrors.Unexpected, ex.Message, file.SystemId);
        }
    }

    private async Task RecordAsync(JobContext context, IngestFile file, Guid fileId, IngestItemResult result, CancellationToken cancellationToken)
    {
        await items.InsertAsync(new JobItem
        {
            JobId = context.JobId,
            FileId = fileId,
            SystemId = result.SystemId ?? file.SystemId,
            SourcePath = file.FullPath,
            Outcome = result.Outcome,
            Reason = result.Reason,
            Details = result.Details,
            GameId = result.GameId,
            Sha1 = result.Sha1,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        }, CancellationToken.None).ConfigureAwait(false);
        if (result.Outcome == IngestOutcome.Error)
            logger.LogError("{Path}: {Outcome} {Reason} {Details}", file.FullPath, result.Outcome, result.Reason, result.Details);
        else
            logger.LogInformation("{Path}: {Outcome} {Reason}", file.FullPath, result.Outcome, result.Reason);
    }
}

/// <summary>An item hit <c>DbWriteFailed</c>: the job ends <c>Failed</c> with this message (§7.2 step 10).</summary>
public sealed class IngestStoppedException(string message) : Exception(message);
