using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Storage;
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

    /// <summary>
    /// IN1: when set (by the endpoint, before <c>StartAsync</c>), only these snapshot files — exact system id + top-level
    /// file name — are processed; the rest are left alone. Safe because the job is transient (one per request) and the
    /// scheduler never starts ingest.
    /// </summary>
    public IReadOnlySet<(string SystemId, string Name)>? Selection { get; set; }

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
            // Best effort, like Review's work cleanup: a leftover never changes the job's outcome.
            try
            {
                var owned = ExternalRomPath.ClassifyDirectory(paths.Value.Cache, work);
                if (owned.Status == ExternalPathStatus.Found)
                    Directory.Delete(owned.Path, recursive: true);
                else if (owned.Status != ExternalPathStatus.Missing)
                    logger.LogWarning("Work folder {Work} was not cleaned up: {Details}", work, owned.Details); // never walk into a replacement
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Work folder {Work} could not be removed: {Error}", work, ex.Message);
            }
        }
    }

    private async Task<object?> RunCoreAsync(JobContext context, string work, CancellationToken cancellationToken)
    {
        // Step 4
        context.Progress.SetCurrentItem(SyncingLibrary);
        await dbSync.SyncAllAsync(cancellationToken).ConfigureAwait(false);

        // Step 5
        var stale = (await reviews.ListPendingSourcesAsync(cancellationToken).ConfigureAwait(false))
            .Where(IsGone).ToList();
        if (stale.Count > 0)
        {
            await reviews.DeleteAsync(stale.Select(r => r.Id).ToList(), cancellationToken).ConfigureAwait(false);
            foreach (var row in stale)
                logger.LogInformation("Review item {Id} removed: {Path} no longer exists", row.Id, row.ArchivePath ?? row.SourcePath);
        }

        // Step 6
        var snapshot = scanner.Scan().SelectMany(s => s.Files)
            .Where(f => Selection is null || Selection.Contains((f.SystemId, f.Name))).ToList();
        context.Progress.SetTotal(snapshot.Count);
        var run = new Run(context, IngestOutcome.All.ToDictionary(o => o, _ => 0), new CircuitBreaker()); // one breaker per job (§10.2)
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var file in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Progress.SetCurrentItem(file.Name);
            var external = systems.ById(file.SystemId) is { EmbedRomsInZip: false };
            if (external && ExternalRomName.Scalars(file.FullPath) > ExternalRomName.MaxSourcePathScalars)
            {
                // I1: the source_path columns can't hold it, so nothing may refer to this file; it is left untouched.
                logger.LogError("{Path}: longer than the {Limit}-character source path limit; left in place", file.FullPath, ExternalRomName.MaxSourcePathScalars);
                await ItemAsync(run, file with { FullPath = ExternalRomName.CutScalars(file.FullPath, ExternalRomName.MaxSourcePathScalars) },
                    IngestItemResult.Error(IngestErrors.IoError, $"the path is longer than {ExternalRomName.MaxSourcePathScalars} characters", file.SystemId)).ConfigureAwait(false);
            }
            else if (external && ArchiveExtractor.IsArchive(file.Name))
                await ExternalArchiveAsync(run, file, Path.Combine(work, Guid.NewGuid().ToString())).ConfigureAwait(false); // its own fresh age check
            else if (external)
                await ItemAsync(run, file, null, CancellationToken.None).ConfigureAwait(false); // I2: its own fresh age check; a started item finishes
            else if (now - file.ModifiedAtUtc < MinFileAge)
                await ItemAsync(run, file, new IngestItemResult(IngestOutcome.Skipped, IngestReason.TooRecent, SystemId: file.SystemId)).ConfigureAwait(false);
            else if (ArchiveExtractor.IsArchive(file.Name))
                await ArchiveAsync(run, file, Path.Combine(work, Guid.NewGuid().ToString())).ConfigureAwait(false);
            else
                await ItemAsync(run, file, null, cancellationToken).ConfigureAwait(false);
            context.Progress.ItemDone();
        }

        return run.Counts;
    }

    /// <summary>
    /// Step 5. An external source (the container for an archive entry) is pruned only when it is confirmed absent:
    /// an unsafe path or an IO or permission failure keeps its row (spec I4).
    /// </summary>
    private bool IsGone(PendingReviewSource row)
    {
        var path = row.ArchivePath ?? row.SourcePath;
        if (systems.ById(row.SystemId) is not { EmbedRomsInZip: false })
            return !File.Exists(path);
        var check = ExternalRomPath.ClassifyFile(paths.Value.Ingest, path);
        if (check.Status is ExternalPathStatus.Unsafe or ExternalPathStatus.Unreadable)
            logger.LogWarning("Review item {Id} kept: its source {Path} can't be checked safely: {Details}", row.Id, path, check.Details);
        return check.Status == ExternalPathStatus.Missing;
    }

    private sealed record Run(JobContext Context, Dictionary<string, int> Counts, CircuitBreaker Breaker);

    /// <summary>One item: processes it (unless <paramref name="known"/>), records its row; null = left in ingest.</summary>
    private async Task<IngestItemResult?> ItemAsync(Run run, IngestFile file, IngestItemResult? known, CancellationToken cancellationToken = default)
    {
        var fileId = Guid.NewGuid();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["FileId"] = fileId });
        var result = known ?? await ProcessAsync(file, run.Context.JobId, run.Context.Actor, run.Breaker, cancellationToken, run.Context.Progress).ConfigureAwait(false);
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
            await ItemAsync(run, archive, pipeline.FailFile(archive, run.Context.Actor, ex.Reason, ex.Message)).ConfigureAwait(false);
            return;
        }
        if (entries.Count == 0)
        {
            // shortcut: §7.3 doesn't say; an archive with no ROM entries is treated as unsupported rather than deleted.
            await ItemAsync(run, archive, pipeline.FailFile(archive, run.Context.Actor, IngestFailures.UnsupportedType, "the archive holds no ROM entries")).ConfigureAwait(false);
            return;
        }

        ApplyFate(archive, run.Context.Actor, await EntriesAsync(run, archive, entries).ConfigureAwait(false));
    }

    /// <summary>§3.1: an archive counts as one file, so every entry finishes even after a cancel.</summary>
    private async Task<List<(string Entry, IngestItemResult? Result)>> EntriesAsync(Run run, IngestFile archive, IReadOnlyList<ExtractedEntry> entries,
        ExternalFileSnapshot? archiveSnapshot = null)
    {
        var outcomes = new List<(string Entry, IngestItemResult? Result)>();
        foreach (var entry in entries)
        {
            run.Context.Progress.SetCurrentItem($"{archive.Name} › {entry.EntryPath}");
            var item = new IngestFile(archive.SystemId, entry.ExtractedPath, entry.FileName, entry.SizeBytes, archive.ModifiedAtUtc)
            {
                ArchivePath = archive.FullPath,
                ArchiveEntry = entry.EntryPath,
                ArchiveSnapshot = archiveSnapshot,
            };
            outcomes.Add((entry.EntryPath, await ItemAsync(run, item, null, CancellationToken.None).ConfigureAwait(false)));
        }
        return outcomes;
    }

    /// <summary>
    /// I1 for an external system's archive: it is read through one checked handle, and its entries are only
    /// processed when that handle and the path still show the file that was listed. Anything else is one item for
    /// the archive, which stays where it is.
    /// </summary>
    private async Task ExternalArchiveAsync(Run run, IngestFile archive, string workDir)
    {
        Task One(IngestItemResult result) => ItemAsync(run, archive, result);
        IngestItemResult Io(string? details) => IngestItemResult.Error(IngestErrors.IoError, details, archive.SystemId);
        var tooRecent = new IngestItemResult(IngestOutcome.Skipped, IngestReason.TooRecent, SystemId: archive.SystemId);
        var family = systems.ByFamily(systems.ById(archive.SystemId)!.Family);

        var opened = ExternalRomPath.OpenRead(paths.Value.Ingest, archive.FullPath, FileShare.Read | FileShare.Delete);
        if (opened.Status != ExternalPathStatus.Found)
        {
            await One(Io(opened.Details)).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<ExtractedEntry> entries = [];
        ArchiveRejectedException? rejected = null;
        var snapshot = opened.Snapshot!;
        using (var stream = opened.Stream!)
        {
            if (snapshot.Length != archive.SizeBytes || snapshot.LastWriteTimeUtc != archive.ModifiedAtUtc
                || time.GetUtcNow().UtcDateTime - snapshot.LastWriteTimeUtc < MinFileAge)
            {
                await One(tooRecent).ConfigureAwait(false);
                return;
            }

            var workCheck = ExternalRomPath.CreateDirectory(paths.Value.Cache, workDir);
            if (workCheck.Status != ExternalPathStatus.Found)
            {
                await One(Io(workCheck.Details)).ConfigureAwait(false);
                return;
            }
            try
            {
                entries = extractor.Extract(stream, archive.Name, workCheck.Path, ext =>
                    family.FirstOrDefault(h => h.RomExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))?.MaxRomSizeBytes);
            }
            catch (ArchiveRejectedException ex)
            {
                rejected = ex;
            }
            catch (ArchiveOutputException ex)
            {
                await One(Io(ex.Message)).ConfigureAwait(false);
                return;
            }

            // Whatever was extracted came from the handle; the entries only count if the path still names that file.
            var current = ExternalRomPath.ClassifyFile(paths.Value.Ingest, archive.FullPath);
            if (current.Status != ExternalPathStatus.Found)
            {
                await One(Io(current.Details)).ConfigureAwait(false);
                return;
            }
            if (current.Snapshot != snapshot || ExternalRomPath.SnapshotOf(stream) != snapshot)
            {
                await One(tooRecent).ConfigureAwait(false);
                return;
            }
        }

        // The archive is only moved or deleted while its path still names the file that was extracted. Otherwise
        // it stays, with one more item for the archive itself; the entries keep the outcomes they already have.
        async Task<bool> StillTheArchiveAsync()
        {
            var current = ExternalRomPath.ClassifyFile(paths.Value.Ingest, archive.FullPath);
            if (current.Status == ExternalPathStatus.Found && current.Snapshot == snapshot)
                return true;
            await One(current.Status == ExternalPathStatus.Found ? tooRecent : Io(current.Details)).ConfigureAwait(false);
            return false;
        }

        var actor = run.Context.Actor;
        if (rejected is not null || entries.Count == 0)
        {
            if (await StillTheArchiveAsync().ConfigureAwait(false))
            {
                await One(rejected is not null
                    ? pipeline.FailFile(archive, actor, rejected.Reason, rejected.Message, progress: run.Context.Progress)
                    : pipeline.FailFile(archive, actor, IngestFailures.UnsupportedType, "the archive holds no ROM entries", progress: run.Context.Progress)).ConfigureAwait(false);
            }
            return;
        }

        var outcomes = await EntriesAsync(run, archive, entries, snapshot).ConfigureAwait(false);
        var fate = FateOf([.. outcomes.Select(o => o.Result)]);
        if (fate == ArchiveFate.Keep)
        {
            logger.LogInformation("{Path}: kept in ingest (an entry needs review, was skipped or hit an error)", archive.FullPath);
            return;
        }
        if (!await StillTheArchiveAsync().ConfigureAwait(false))
            return;

        IngestItemResult? error = null;
        if (fate == ArchiveFate.Failed)
            error = pipeline.MoveToFailed(archive, actor, "EntriesFailed", EntryDetails(outcomes), run.Context.Progress);
        else if (fate == ArchiveFate.Duplicates)
            error = pipeline.MoveToDuplicates(archive, actor, run.Context.Progress);
        else
        {
            try
            {
                File.Delete(archive.FullPath);
                pipeline.RecordArchiveDeleted(archive, actor);
                logger.LogInformation("{Path}: every entry ingested; archive deleted", archive.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = Io($"couldn't delete the archive: {ex.Message}");
            }
        }
        if (error is not null)
            await One(error).ConfigureAwait(false);
    }

    /// <summary>
    /// §7.3: any entry <c>Review</c>/<c>Error</c> (or still waiting) → the archive stays; else any <c>Failed</c> →
    /// <c>_failed/</c> with every entry's outcome in the <c>.error.txt</c>; else all <c>Duplicate</c> → <c>_duplicates/</c>;
    /// else deleted (<c>Repaired</c> counts as <c>Ingested</c>).
    /// </summary>
    private void ApplyFate(IngestFile archive, AuditActor actor, IReadOnlyList<(string Entry, IngestItemResult? Result)> outcomes)
    {
        var fate = FateOf([.. outcomes.Select(o => o.Result)]);
        if (fate == ArchiveFate.Keep)
        {
            logger.LogInformation("{Path}: kept in ingest (an entry needs review or hit an error)", archive.FullPath);
            return;
        }
        if (fate == ArchiveFate.Failed)
        {
            pipeline.MoveToFailed(archive, actor, "EntriesFailed", EntryDetails(outcomes));
            return;
        }
        if (fate == ArchiveFate.Duplicates)
        {
            pipeline.MoveToDuplicates(archive, actor);
            return;
        }
        try
        {
            File.Delete(archive.FullPath);
            pipeline.RecordArchiveDeleted(archive, actor);
            logger.LogInformation("{Path}: every entry ingested; archive deleted", archive.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: couldn't delete the archive ({Error}); the next run sees duplicates", archive.FullPath, ex.Message);
        }
    }

    public enum ArchiveFate
    {
        Keep,
        Failed,
        Duplicates,
        Delete,
    }

    /// <summary>
    /// §7.3 as a decision. An entry that was <c>Skipped</c> (an external entry that was <c>TooRecent</c>, I1) keeps
    /// the archive like <c>Review</c> or <c>Error</c> does, even when other entries committed.
    /// </summary>
    public static ArchiveFate FateOf(IReadOnlyList<IngestItemResult?> results)
    {
        var kinds = results.Select(r => r is { ResolvedArchiveDuplicate: true } ? IngestOutcome.Ingested : r?.Outcome).ToList();
        if (kinds.Any(k => k is null or IngestOutcome.Review or IngestOutcome.Error or IngestOutcome.Skipped))
            return ArchiveFate.Keep;
        if (kinds.Contains(IngestOutcome.Failed))
            return ArchiveFate.Failed;
        return kinds.All(k => k == IngestOutcome.Duplicate) ? ArchiveFate.Duplicates : ArchiveFate.Delete;
    }

    private static string EntryDetails(IReadOnlyList<(string Entry, IngestItemResult? Result)> outcomes) =>
        "entries:\n" + string.Join("\n", outcomes.Select(o => $"  {o.Entry}: {o.Result!.Outcome} {o.Result.Reason}".TrimEnd()));

    private async Task<IngestItemResult?> ProcessAsync(IngestFile file, Guid jobId, AuditActor actor, CircuitBreaker breaker,
        CancellationToken cancellationToken, JobProgress progress)
    {
        try
        {
            return await pipeline.ProcessAsync(file, jobId, actor, breaker, cancellationToken, progress).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "{Path}: unexpected failure", file.FullPath);
            var result = IngestItemResult.Error(IngestErrors.Unexpected, ex.Message, file.SystemId);
            if (systems.ById(file.SystemId) is { EmbedRomsInZip: false })
                return result; // I4: an external source's Pending row stays
            await reviews.CompleteSourceAsync(file.SourcePath, file.ArchiveEntry, result, jobId, time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
            return result;
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
