using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>
/// The <c>Ingest</c> job (spec §7.1): refuses with <c>DatsMissing</c> before any row exists, then the DB sync pre-pass,
/// stale <c>Pending</c> review rows, and the snapshot (files younger than 10 s are <c>Skipped</c> <c>TooRecent</c>).
/// Loose files go through <see cref="IngestPipeline"/>; archives are unpacked into <c>cache/work/&lt;jobId&gt;/</c> and each
/// ROM entry is its own item, then the archive's fate is applied (§7.3). One <c>job_items</c> row per item. An item's
/// unexpected exception is <c>Error</c> <c>Unexpected</c> and the next item continues; <c>DbWriteFailed</c> stops the job
/// as <c>Failed</c>. Cancel is checked between files; an archive always finishes.
/// </summary>
public sealed class IngestJob(
    DatCache dats,
    ISystemRegistry systems,
    DbSync dbSync,
    IIngestReviewRepository reviews,
    IJobItemRepository items,
    IngestFileScanner scanner,
    IngestPipeline pipeline,
    ArchiveExtractor extractor,
    IOptions<PathsOptions> paths,
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
        var work = Path.Combine(paths.Value.Cache, CrashRecovery.WorkFolder, context.JobId.ToString());
        try
        {
            return await RunCoreAsync(context, work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
    }

    private async Task<object?> RunCoreAsync(JobContext context, string work, CancellationToken cancellationToken)
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
        var run = new Run(context, IngestOutcome.All.ToDictionary(o => o, _ => 0), new CircuitBreaker()); // one breaker per job (§10.2)
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var file in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Progress.SetCurrentItem(file.Name);
            if (now - file.ModifiedAtUtc < MinFileAge)
                await ItemAsync(run, file, new IngestItemResult(IngestOutcome.Skipped, IngestReason.TooRecent, SystemId: file.SystemId)).ConfigureAwait(false);
            else if (ArchiveExtractor.IsArchive(file.Name))
                await ArchiveAsync(run, file, Path.Combine(work, Guid.NewGuid().ToString())).ConfigureAwait(false);
            else
                await ItemAsync(run, file, null, cancellationToken).ConfigureAwait(false);
            context.Progress.ItemDone();
        }

        return run.Counts;
    }

    private sealed record Run(JobContext Context, Dictionary<string, int> Counts, CircuitBreaker Breaker);

    /// <summary>One item: processes it (unless <paramref name="known"/>), records its row; null = left in ingest.</summary>
    private async Task<IngestItemResult?> ItemAsync(Run run, IngestFile file, IngestItemResult? known, CancellationToken cancellationToken = default)
    {
        var fileId = Guid.NewGuid();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["FileId"] = fileId });
        var result = known ?? await ProcessAsync(file, run.Context.JobId, run.Breaker, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return null;

        await RecordAsync(run.Context, file, fileId, result).ConfigureAwait(false);
        run.Counts[result.Outcome]++;
        if (result.Reason == IngestErrors.DbWriteFailed)
            throw new IngestStoppedException($"{file.Name}: DB write failed ({result.Details}); the job stopped so no later file groups against a stale DB");
        return result;
    }

    /// <summary>§7.2 step 1a + §7.3: unpack, one item per ROM entry, then the archive's fate.</summary>
    private async Task ArchiveAsync(Run run, IngestFile archive, string workDir)
    {
        var family = systems.ByFamily(systems.ById(archive.SystemId)!.Family);
        IReadOnlyList<ExtractedEntry> entries;
        try
        {
            entries = extractor.Extract(archive.FullPath, workDir, ext =>
                family.FirstOrDefault(h => h.RomExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))?.MaxRomSizeBytes);
        }
        catch (ArchiveRejectedException ex)
        {
            await ItemAsync(run, archive, pipeline.FailFile(archive, run.Context.JobId, ex.Reason, ex.Message)).ConfigureAwait(false);
            return;
        }
        if (entries.Count == 0)
        {
            // shortcut: §7.3 doesn't say; an archive with no ROM entries is treated as unsupported rather than deleted.
            await ItemAsync(run, archive, pipeline.FailFile(archive, run.Context.JobId, IngestFailures.UnsupportedType, "the archive holds no ROM entries")).ConfigureAwait(false);
            return;
        }

        // §3.1: an archive counts as one file, so every entry finishes even after a cancel.
        var outcomes = new List<(string Entry, IngestItemResult? Result)>();
        foreach (var entry in entries)
        {
            run.Context.Progress.SetCurrentItem($"{archive.Name} › {entry.EntryPath}");
            var item = new IngestFile(archive.SystemId, entry.ExtractedPath, entry.FileName, entry.SizeBytes, archive.ModifiedAtUtc)
            {
                ArchivePath = archive.FullPath,
                ArchiveEntry = entry.EntryPath,
            };
            outcomes.Add((entry.EntryPath, await ItemAsync(run, item, null, CancellationToken.None).ConfigureAwait(false)));
        }

        ApplyFate(archive, run.Context.JobId, outcomes);
    }

    /// <summary>
    /// §7.3: any entry <c>Review</c>/<c>Error</c> (or still waiting) → the archive stays; else any <c>Failed</c> →
    /// <c>_failed/</c> with every entry's outcome in the <c>.error.txt</c>; else all <c>Duplicate</c> → <c>_duplicates/</c>;
    /// else deleted (<c>Repaired</c> counts as <c>Ingested</c>).
    /// </summary>
    private void ApplyFate(IngestFile archive, Guid jobId, IReadOnlyList<(string Entry, IngestItemResult? Result)> outcomes)
    {
        var kinds = outcomes.Select(o => o.Result?.Outcome).ToList();
        if (kinds.Any(k => k is null or IngestOutcome.Review or IngestOutcome.Error))
        {
            logger.LogInformation("{Path}: kept in ingest (an entry needs review or hit an error)", archive.FullPath);
            return;
        }
        if (kinds.Contains(IngestOutcome.Failed))
        {
            var details = string.Join("\n", outcomes.Select(o => $"  {o.Entry}: {o.Result!.Outcome} {o.Result.Reason}".TrimEnd()));
            pipeline.MoveToFailed(archive, jobId, "EntriesFailed", "entries:\n" + details);
            return;
        }
        if (kinds.All(k => k == IngestOutcome.Duplicate))
        {
            pipeline.MoveToDuplicates(archive);
            return;
        }
        try
        {
            File.Delete(archive.FullPath);
            logger.LogInformation("{Path}: every entry ingested; archive deleted", archive.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: couldn't delete the archive ({Error}); the next run sees duplicates", archive.FullPath, ex.Message);
        }
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

    private async Task RecordAsync(JobContext context, IngestFile file, Guid fileId, IngestItemResult result)
    {
        await items.InsertAsync(new JobItem
        {
            JobId = context.JobId,
            FileId = fileId,
            SystemId = result.SystemId ?? file.SystemId,
            SourcePath = file.ArchivePath ?? file.FullPath,
            ArchiveEntry = file.ArchiveEntry,
            Outcome = result.Outcome,
            Reason = result.Reason,
            Details = result.Details,
            GameId = result.GameId,
            Sha1 = result.Sha1,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        }, CancellationToken.None).ConfigureAwait(false);
        var name = file.ArchiveEntry is null ? file.FullPath : $"{file.ArchivePath} › {file.ArchiveEntry}";
        if (result.Outcome == IngestOutcome.Error)
            logger.LogError("{Path}: {Outcome} {Reason} {Details}", name, result.Outcome, result.Reason, result.Details);
        else
            logger.LogInformation("{Path}: {Outcome} {Reason}", name, result.Outcome, result.Reason);
    }
}

/// <summary>An item hit <c>DbWriteFailed</c>: the job ends <c>Failed</c> with this message (§7.2 step 10).</summary>
public sealed class IngestStoppedException(string message) : Exception(message);
