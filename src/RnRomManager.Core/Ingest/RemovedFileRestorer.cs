using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>D5: a restored Deleted ROM under its new pending name in <c>ingest/&lt;system&gt;/</c>.</summary>
public sealed record RestoredFile(string System, string Name);

/// <param name="Code"><c>JobRunning</c> when the lock was busy (nothing moved), else null.</param>
public sealed record RemovedRestoreResult(IReadOnlyList<RestoredFile> Restored, IReadOnlyList<IngestDeleteRef> Failed, string? Code = null);

/// <summary>
/// Deleted ROMs restore (D5): moves files from <c>ingest/&lt;system&gt;/_removed/</c> back to <c>ingest/&lt;system&gt;/</c>
/// under the global job lock, never replacing a pending file (a free <c> (N)</c> name instead). The caller starts the ingest job.
/// </summary>
public sealed class RemovedFileRestorer(JobLock jobLock, ISystemRegistry systems, IOptions<PathsOptions> paths, IAuditLog audit,
    AuditPaths auditPaths, ILogger<RemovedFileRestorer> logger)
{
    /// <param name="files">Already checked by <see cref="IngestFileDeleter.Validate"/> with folder <c>removed</c>; duplicates are ignored.</param>
    public RemovedRestoreResult Restore(IReadOnlyList<IngestDeleteRef> files, AuditActor actor)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new([], [], "JobRunning");
        var seen = new HashSet<IngestDeleteRef>();
        var restored = new List<RestoredFile>();
        var failed = new List<IngestDeleteRef>();
        foreach (var file in files)
        {
            if (!seen.Add(file))
                continue;
            if (RestoreOne(file, actor) is { } name)
                restored.Add(new(file.System!, name));
            else
                failed.Add(file);
        }
        logger.LogInformation("User {UserId} restored {Restored} deleted ROM(s), {Failed} failed", actor.UserId, restored.Count, failed.Count);
        return new(restored, failed);
    }

    /// <returns>The new pending file name, or null when the file is missing or the move failed.</returns>
    private string? RestoreOne(IngestDeleteRef file, AuditActor actor)
    {
        var root = paths.Value.Ingest;
        var source = SafePath.Resolve(root, Path.Combine(file.System!, StorageFolders.RemovedFolder, file.Name!));
        var folder = SafePath.Resolve(root, file.System!);
        try
        {
            var destination = systems.ById(file.System!) is { EmbedRomsInZip: false }
                ? MoveChecked(root, source, folder)
                : MovePlain(source, folder);
            if (destination is null)
                return null;
            audit.Record(new AuditEvent(AuditActions.RemovedRestored, actor, file.System, Path: auditPaths.Of(source),
                TargetPath: auditPaths.Of(destination)));
            return Path.GetFileName(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Restoring deleted ROM '{Path}' failed", source);
            return null;
        }
    }

    private string? MovePlain(string source, string folder)
    {
        if (!File.Exists(source))
        {
            logger.LogWarning("Deleted ROM '{Path}' is gone; nothing to restore", source);
            return null;
        }
        var destination = Path.Combine(folder, FileNameRules.FirstFreeIn(folder, Path.GetFileName(source)));
        File.Move(source, destination, overwrite: false);
        return destination;
    }

    /// <summary>W6/G4 for an external system: checked folder and source, a budgeted free name, a move that never replaces.</summary>
    private string? MoveChecked(string root, string source, string folder)
    {
        var target = ExternalRomPath.ClassifyDirectory(root, folder);
        var check = ExternalRomPath.ClassifyFile(root, source);
        if (target.Status != ExternalPathStatus.Found || check.Status != ExternalPathStatus.Found)
        {
            logger.LogWarning("Deleted ROM '{Path}' was not restored: {Details}", source, target.Details ?? check.Details);
            return null;
        }
        if (ExternalRomName.AllocateSidePath(target.Path, Path.GetFileName(source)) is not { } destination)
        {
            logger.LogWarning("Deleted ROM '{Path}' was not restored: no free name fits in {Folder}", source, target.Path);
            return null;
        }
        var moved = ExternalRomMove.Move(root, check.Path, destination, check.Snapshot!, logger);
        if (moved.Status == ExternalMoveStatus.Moved)
            return moved.Path;
        logger.LogWarning("Deleted ROM '{Path}' was not restored: {Details}", source, moved.Details);
        return null;
    }
}
