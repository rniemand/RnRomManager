using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>Moves an ingest source aside (<c>_failed/</c>, <c>_duplicates/</c>) or queues it for review, for <see cref="IngestPipeline"/>.</summary>
internal sealed class IngestFileMoves(
    IngestPipeline pipeline,
    ISystemRegistry systems,
    IIngestReviewRepository reviews,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    AuditPaths auditPaths,
    TimeProvider time,
    ILogger<IngestPipeline> logger)
{
    /// <summary>
    /// A file problem: <c>Failed</c>, and the file moves to <c>_failed/</c> with <c>&lt;name&gt;.error.txt</c> (spec §5).
    /// An archive entry stays put: its archive's fate decides (§7.3).
    /// </summary>
    /// <param name="progress">Gets a <c>Copying</c> step when an external file's move falls back to a copy.</param>
    internal IngestItemResult FailFile(
        IngestFile file,
        AuditActor actor,
        string reason,
        string details,
        string? sha1 = null,
        JobProgress? progress = null)
    {
        logger.LogWarning("{Path}: Failed {Reason}: {Details}", file.ArchiveEntry is null ? file.FullPath : $"{file.ArchivePath} › {file.ArchiveEntry}", reason, details);
        var moveError = file.ArchiveEntry is null ? MoveToFailed(file, actor, reason, details, progress) : null;
        return moveError ?? new IngestItemResult(IngestOutcome.Failed, reason, details, file.SystemId, Sha1: sha1);
    }

    /// <summary>
    /// Moves the file to <c>_failed/</c> and writes its <c>.error.txt</c> (reason, details, job — or <c>review by
    /// &lt;username&gt;</c> without one — UTC time). Records <c>MovedToFailed</c>.
    /// </summary>
    /// <returns>An <c>Error IoError</c> result when the move failed, else null.</returns>
    internal IngestItemResult? MoveToFailed(
        IngestFile file,
        AuditActor actor,
        string reason,
        string details,
        JobProgress? progress = null)
    {
        var moved = MoveAside(file, StorageFolders.FailedFolder, progress);
        if (moved.Error is not null)
            return moved.Error;
        var fromReview = actor.JobId is null;
        audit.Record(new AuditEvent(AuditActions.MovedToFailed, actor, file.SystemId, Path: auditPaths.Of(file.FullPath),
            TargetPath: auditPaths.Of(moved.Path), Details: fromReview ? "review dismiss" : reason));
        var by = actor.JobId is { } jobId ? jobId.ToString() : $"review by {actor.Username}";
        try
        {
            // External systems never overwrite: the name was reserved, so an occupant is someone else's file.
            using var diagnostic = new StreamWriter(new FileStream(moved.Path + DiagnosticSuffix, IsExternal(file) ? FileMode.CreateNew : FileMode.Create));
            diagnostic.Write($"reason: {reason}\ndetails: {details}\njob: {by}\ntime: {time.GetUtcNow().UtcDateTime:O}\nsource: {file.FullPath}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: couldn't write the .error.txt: {Error}", moved.Path, ex.Message);
        }
        return null;
    }

    /// <returns>An <c>Error IoError</c> result when the move failed, else null.</returns>
    internal IngestItemResult? MoveToDuplicates(IngestFile file, AuditActor actor, JobProgress? progress = null)
    {
        var moved = MoveAside(file, StorageFolders.DuplicatesFolder, progress);
        if (moved.Error is null)
            audit.Record(new AuditEvent(AuditActions.MovedToDuplicates, actor, file.SystemId, Path: auditPaths.Of(file.FullPath),
                TargetPath: auditPaths.Of(moved.Path)));
        return moved.Error;
    }

    internal const string DiagnosticSuffix = ".error.txt";

    internal bool IsExternal(IngestFile file) => systems.ById(file.SystemId) is { EmbedRomsInZip: false };

    /// <summary>Moves the source into <c>ingest/&lt;system&gt;/&lt;folder&gt;/</c>, with a ` (N)` suffix on a clash (any case).</summary>
    internal (string Path, IngestItemResult? Error) MoveAside(IngestFile file, string folder, JobProgress? progress)
    {
        var directory = Path.Combine(Path.GetDirectoryName(file.FullPath)!, folder);
        if (IsExternal(file))
            return MoveAsideChecked(file, folder, directory, progress);
        try
        {
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, FileNameRules.FirstFreeIn(directory, file.Name));
            File.Move(file.FullPath, target);
            return (target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ("", IngestItemResult.Error(IngestErrors.IoError, $"couldn't move to {folder}/: {ex.Message}", file.SystemId));
        }
    }

    /// <summary>
    /// W6 for an external system: the side folder and the source are checked (no links, a regular file), the name
    /// is budgeted with its staging and diagnostic names, and the move never replaces anything.
    /// </summary>
    internal (string Path, IngestItemResult? Error) MoveAsideChecked(IngestFile file, string folder, string directory, JobProgress? progress)
    {
        (string, IngestItemResult?) Fail(string? details) =>
            ("", IngestItemResult.Error(IngestErrors.IoError, $"couldn't move to {folder}/: {details}", file.SystemId));

        var root = paths.Value.Ingest;
        try
        {
            var target = ExternalRomPath.EnsureDirectory(root, directory);
            if (target.Status != ExternalPathStatus.Found)
                return Fail(target.Details);
            var source = ExternalRomPath.ClassifyFile(root, file.FullPath);
            if (source.Status != ExternalPathStatus.Found)
                return Fail(source.Details);
            if (ExternalRomName.AllocateSidePath(target.Path, file.Name, folder == StorageFolders.FailedFolder ? DiagnosticSuffix : null) is not { } destination)
                return Fail($"no name for {file.Name} fits in {target.Path}");

            var moved = Tracked(progress, JobStepPhases.Copying, source.Snapshot!.Length, onCopied =>
                ExternalRomMove.Move(root, source.Path, destination, source.Snapshot!, logger, hooks: pipeline.ExternalHooks?.Aside, onCopied: onCopied));
            return moved.Status == ExternalMoveStatus.Moved ? (moved.Path!, null) : Fail(moved.Details);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// §8.4: an unmatched file whose extension doesn't fit its header classification is renamed in place to the
    /// classified handler's first extension (<c>Warn</c>).
    /// </summary>
    internal IngestFile RenameToClassifiedExtension(
        IngestFile file,
        RomHeader? header,
        IReadOnlyList<ISystemHandler> family,
        AuditActor actor)
    {
        var classified = family.FirstOrDefault(h => h.Id == header?.ClassifiedSystemId);
        if (classified is null || classified.RomExtensions.Contains(Path.GetExtension(file.Name), StringComparer.OrdinalIgnoreCase))
            return file;
        var directory = Path.GetDirectoryName(file.FullPath)!;
        var renamed = FileNameRules.FirstFreeIn(directory, Path.GetFileNameWithoutExtension(file.Name) + classified.RomExtensions[0]);
        try
        {
            File.Move(file.FullPath, Path.Combine(directory, renamed));
            audit.Record(new AuditEvent(AuditActions.ExtensionRenamed, actor, file.SystemId, Path: auditPaths.Of(file.FullPath),
                TargetPath: auditPaths.Of(Path.Combine(directory, renamed))));
            logger.LogWarning("{Path}: header says {System}; renamed to {Name}", file.FullPath, classified.Id, renamed);
            return file with { FullPath = Path.Combine(directory, renamed), Name = renamed };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: couldn't rename to {Name}: {Error}", file.FullPath, renamed, ex.Message);
        }
        return file;
    }

    internal async Task<IngestItemResult> QueueReviewAsync(
        IngestFile file,
        string originalName,
        string systemId,
        string reason,
        RomHashes hashes,
        long sizeBytes,
        RomHeader? header,
        Guid jobId,
        IReadOnlyList<Suggestion> suggestions,
        object? identification,
        IReadOnlyList<Guid>? candidates,
        CancellationToken cancellationToken)
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await reviews.UpsertAsync(new ReviewItem
        {
            SystemId = systemId, Reason = reason, SourcePath = file.SourcePath, ArchivePath = file.ArchivePath,
            ArchiveEntry = file.ArchiveEntry, OriginalFileName = originalName, SizeBytes = sizeBytes,
            Crc32 = hashes.Crc32, Md5 = hashes.Md5, Sha1 = hashes.Sha1,
            Header = header is null ? null : JsonSerializer.Serialize(header.Fields, json),
            Suggestions = JsonSerializer.Serialize(suggestions, json),
            DatIdentification = identification is null ? null : JsonSerializer.Serialize(identification, json),
            CandidateGameIds = candidates is null ? null : JsonSerializer.Serialize(candidates, json),
            CreatedAt = time.GetUtcNow().UtcDateTime, LastSeenJobId = jobId,
        }, cancellationToken).ConfigureAwait(false);
        return new IngestItemResult(IngestOutcome.Review, reason, SystemId: systemId, Sha1: hashes.Sha1);
    }

    /// <summary>
    /// Runs <paramref name="move"/> with a copy callback that begins a <paramref name="phase"/> step on its first call
    /// (the copy fallback only; a rename reports nothing) and ends it afterwards (spec B2/B3).
    /// </summary>
    internal ExternalMoveResult Tracked(
        JobProgress? progress,
        string phase,
        long bytesTotal,
        Func<Action<long>?, ExternalMoveResult> move)
    {
        if (progress is null)
            return move(null);
        var started = false;
        try
        {
            return move(done =>
            {
                if (!started)
                {
                    started = true;
                    progress.BeginStep(phase, bytesTotal, time.GetUtcNow().UtcDateTime);
                }
                progress.StepAdvanced(done);
            });
        }
        finally
        {
            if (started)
                progress.EndStep();
        }
    }
}
