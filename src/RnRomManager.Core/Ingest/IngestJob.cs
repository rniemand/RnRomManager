using Microsoft.Extensions.Logging;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>
/// The <c>Ingest</c> job (spec §7.1): refuses with <c>DatsMissing</c> before any row exists, then the DB sync pre-pass,
/// stale <c>Pending</c> review rows, and the snapshot (files younger than 10 s are <c>Skipped</c> <c>TooRecent</c>).
/// The per-file pipeline (§7.2) plugs in at <see cref="ProcessAsync"/>.
/// </summary>
public sealed class IngestJob(
    DatCache dats,
    ISystemRegistry systems,
    DbSync dbSync,
    IIngestReviewRepository reviews,
    IJobItemRepository items,
    IngestFileScanner scanner,
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
        foreach (var file in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Progress.SetCurrentItem(file.Name);
            var fileId = Guid.NewGuid();
            using (logger.BeginScope(new Dictionary<string, object> { ["FileId"] = fileId }))
            {
                var outcome = now - file.ModifiedAtUtc < MinFileAge
                    ? await RecordAsync(context, file, fileId, IngestOutcome.Skipped, IngestReason.TooRecent, cancellationToken).ConfigureAwait(false)
                    : await ProcessAsync(context, file, fileId, cancellationToken).ConfigureAwait(false);
                if (outcome is not null)
                    counts[outcome]++;
            }
            context.Progress.ItemDone();
        }

        return counts;
    }

    /// <summary>§7.2 per-file pipeline. shortcut: not implemented until S26; files are left untouched for the next run.</summary>
    private Task<string?> ProcessAsync(JobContext context, IngestFile file, Guid fileId, CancellationToken cancellationToken)
    {
        _ = (context, fileId, cancellationToken);
        logger.LogDebug("{Path}: waiting for the ingest pipeline", file.FullPath);
        return Task.FromResult<string?>(null);
    }

    private async Task<string?> RecordAsync(
        JobContext context, IngestFile file, Guid fileId, string outcome, string? reason, CancellationToken cancellationToken)
    {
        await items.InsertAsync(new JobItem
        {
            JobId = context.JobId,
            FileId = fileId,
            SystemId = file.SystemId,
            SourcePath = file.FullPath,
            Outcome = outcome,
            Reason = reason,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        }, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("{Path}: {Outcome} {Reason}", file.FullPath, outcome, reason);
        return outcome;
    }
}
