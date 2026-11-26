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

/// <summary>Writes a release into its game's ZIP and places external files (with rollback and repair backups), for <see cref="IngestPipeline"/>.</summary>
internal sealed class IngestPackager(
    IngestPipeline pipeline,
    ILibraryMirror mirror,
    IIngestReviewRepository reviews,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    ILogger<IngestPipeline> logger)
{
    /// <summary>Steps 9–11.</summary>
    /// <param name="afterMirror">Runs once the DB is updated, before the source is deleted.</param>
    /// <param name="reviewAction">Null from ingest; the resolution action from review (the <c>Ingested</c> row's details).</param>
    /// <param name="release">The new release's <c>fileName</c>: the <c>Ingested</c> row's details (I7).</param>
    /// <param name="placement">An external ROM's file, moved next to the ZIP right before the ZIP is written (I4).</param>
    internal async Task<IngestItemResult> PackageAsync(
        IngestFile file,
        LibraryZip? existing,
        string zipPath,
        InfoJsonGame game,
        Dictionary<string, Func<Stream>> entries,
        IngestItemResult result,
        AuditActor actor,
        string? reviewAction,
        string release,
        CancellationToken cancellationToken,
        Func<Guid, Task>? afterMirror = null,
        IReadOnlySet<string>? removedEntries = null,
        ExternalPlacement? placement = null,
        RepairPlan? repair = null,
        IReadOnlyList<SetPlacement>? set = null)
    {
        result = result with { GameId = game.GameId };
        var library = paths.Value.Roms;
        var target = SafePath.Resolve(library, zipPath);
        ExternalMoveResult? moved = null;
        ExternalFileSnapshot? published = null;
        var setMoves = new List<(SetPlacement Member, ExternalMoveResult Moved)>();
        if (set is not null)
        {
            // B7: the set's files move one by one in set order (cue first); a failed move puts back the ones already moved.
            foreach (var member in set)
            {
                var (memberMoved, memberStop) = Place(member.File, member.Placement, target, result with { Sha1 = member.Sha1 });
                if (memberStop is not null)
                {
                    var notes = await RollBackSetAsync(setMoves, memberStop.Details ?? memberStop.Reason ?? "").ConfigureAwait(false);
                    return memberStop with { Details = IngestPipeline.Join(memberStop.Details, notes) };
                }
                setMoves.Add((member, memberMoved!));
            }
            cancellationToken = CancellationToken.None;
        }
        else if (placement is not null)
        {
            if (repair?.ExistingPath is not null && StepAside(repair) is { } refused)
                return IngestItemResult.Error(IngestErrors.IoError, refused, result.SystemId, result.Sha1);
            (moved, var stop) = Place(file, placement, target, result);
            if (stop is not null)
                return stop with { Details = IngestPipeline.Join(stop.Details, RestoreOriginal(repair)) };
            // From the move on nothing may strand the file: the write, the mirror and afterMirror run to the end (I4).
            cancellationToken = CancellationToken.None;
        }

        try
        {
            var placed = set is not null
                ? setMoves.ToDictionary(m => m.Member.Placement.ExternalFileName, m => m.Moved.Snapshot!, StringComparer.OrdinalIgnoreCase)
                : moved is null ? null
                : new Dictionary<string, ExternalFileSnapshot>(StringComparer.OrdinalIgnoreCase) { [placement!.ExternalFileName] = moved.Snapshot! };
            var changes = new LibraryZipChanges(game, entries, removedEntries, placed);
            if (placement is null)
                await pipeline.Writer(game.System).WriteAsync(target, existing, changes, cancellationToken).ConfigureAwait(false);
            else
                published = await pipeline.Writer(game.System).WriteCheckedAsync(library, target, existing, changes, cancellationToken, pipeline.ExternalHooks?.Writer).ConfigureAwait(false);
        }
        catch (Exception ex) when (placement is not null || ex is LibraryZipVerifyException or IOException or UnauthorizedAccessException)
        {
            // I4: any failure of the write puts an external ROM's file back before it is reported.
            if (set is not null)
            {
                var notes = await RollBackSetAsync(setMoves, ex.Message).ConfigureAwait(false);
                logger.LogError(ex, "{Path}: writing {Zip} failed", file.FullPath, zipPath);
                var setReason = ex switch
                {
                    LibraryZipVerifyException => IngestErrors.VerifyFailed,
                    IOException or UnauthorizedAccessException => IngestErrors.IoError,
                    _ => null,
                };
                if (setReason is null)
                    throw;
                return result with { Outcome = IngestOutcome.Error, Reason = setReason, Details = IngestPipeline.Join(ex.Message, notes) };
            }
            var (survivor, restoredAs) = placement is null ? (null, null) : RollBack(file, placement, moved!, repair);
            var reason = ex switch
            {
                LibraryZipVerifyException => IngestErrors.VerifyFailed,
                IOException or UnauthorizedAccessException => IngestErrors.IoError,
                _ => null,
            };
            logger.LogError(ex, "{Path}: writing {Zip} failed", file.FullPath, zipPath);
            if (restoredAs is not null && await RetargetPendingAsync(placement!.Source.Path, restoredAs, result, ex.Message).ConfigureAwait(false) is { } retargetFailed)
                return retargetFailed;
            if (reason is null)
                throw;
            return result with { Outcome = IngestOutcome.Error, Reason = reason, Details = survivor is null ? ex.Message : $"{ex.Message}; {survivor}" };
        }
        // AU4: the ZIP is written, so the files changed even if the DB mirror fails below.
        var repaired = result.Outcome == IngestOutcome.Repaired;
        audit.Record(new AuditEvent(repaired ? AuditActions.ZipRepaired : AuditActions.Ingested, actor, game.System, game.GameId,
            pipeline.AuditSource(file), AuditPaths.Library(zipPath),
            repaired ? null : reviewAction is null ? $"release {release}" : $"review resolve ({reviewAction})"));

        try
        {
            MirrorEntry entry;
            if (published is not null)
            {
                // W1: stamped from the ZIP pipeline write published, and only while the path still names it.
                if (ExternalRomPath.ClassifyFile(library, target).Snapshot != published)
                    throw new IOException($"{zipPath} is no longer the ZIP pipeline write published");
                entry = new MirrorEntry(game, zipPath, published.Length, published.LastWriteTimeUtc);
            }
            else
            {
                var info = new FileInfo(target);
                entry = new MirrorEntry(game, zipPath, info.Length, info.LastWriteTimeUtc);
            }
            await mirror.ReplaceAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Path}: {Zip} was written but the DB update failed", file.FullPath, zipPath);
            return result with { Outcome = IngestOutcome.Error, Reason = IngestErrors.DbWriteFailed, Details = IngestPipeline.Join(ex.Message, RepairOriginalNote(repair)) };
        }
        if (afterMirror is not null)
        {
            try { await afterMirror(game.GameId).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{Path}: post-mirror DB update failed", file.FullPath);
                return result with { Outcome = IngestOutcome.Error, Reason = IngestErrors.DbWriteFailed, Details = IngestPipeline.Join(ex.Message, RepairOriginalNote(repair)) };
            }
        }
        if (repair is not null)
            DeleteBackup(repair);

        if (placement is not null)
        {
            // I5: the source was moved, so there is nothing left to delete.
            logger.LogInformation("{Path}: {Outcome} into {Zip} as {File}", file.FullPath, result.Outcome, zipPath, placement.ExternalFileName);
            return result;
        }
        try
        {
            File.Delete(file.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: ingested into {Zip} but couldn't be deleted ({Error}); the next run sees a duplicate", file.FullPath, zipPath, ex.Message);
        }
        logger.LogInformation("{Path}: {Outcome} into {Zip}", file.FullPath, result.Outcome, zipPath);
        return result;
    }

    /// <summary>
    /// B7: puts every moved set file back to its ingest path (or a <c> (2)</c>-style sibling, noted), last moved first,
    /// and makes a Pending row follow a file that came back under another name.
    /// </summary>
    /// <returns>The notes of every file that didn't go back exactly where it was; null when all did.</returns>
    internal async Task<string?> RollBackSetAsync(List<(SetPlacement Member, ExternalMoveResult Moved)> moves, string failure)
    {
        string? notes = null;
        foreach (var (member, movedMember) in Enumerable.Reverse(moves))
        {
            var (note, restoredAs) = RollBack(member.File, member.Placement, movedMember, repair: null);
            if (note is not null)
                notes = IngestPipeline.Join(notes, $"{member.File.Name}: {note}");
            if (restoredAs is not null && await RetargetPendingAsync(member.Placement.Source.Path, restoredAs,
                    new IngestItemResult(IngestOutcome.Error, Sha1: member.Sha1), failure).ConfigureAwait(false) is { } retargetFailed)
                notes = IngestPipeline.Join(notes, retargetFailed.Details);
        }
        moves.Clear();
        return notes;
    }

    /// <summary>
    /// I4: verified loose bytes went back under another ingest name, so a Pending row of the original path and SHA1
    /// follows them; its original file name and queued identification are kept. This runs to the end whatever the
    /// caller's token says, under the lock the caller already holds.
    /// </summary>
    /// <returns>A <c>DbWriteFailed</c> result when the row couldn't be moved; null when it was (or there was none).</returns>
    internal async Task<IngestItemResult?> RetargetPendingAsync(
        string original,
        string restoredAs,
        IngestItemResult result,
        string writeFailure)
    {
        try
        {
            await reviews.RetargetPendingAsync(original, result.Sha1!, restoredAs, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Original}: the ZIP write failed ({WriteFailure}) and the file went back as {Restored}, but its Pending row still names " +
                "the original path. Once that path is free, move {Restored} back to {Original}.", original, writeFailure, restoredAs, restoredAs, original);
            return result with
            {
                Outcome = IngestOutcome.Error, Reason = IngestErrors.DbWriteFailed,
                Details = $"{writeFailure}; moved back as {restoredAs}, but the Pending row still names {original}: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// I4: moves an external ROM's file to its name next to <paramref name="target"/>. The source must still be the
    /// file that was hashed; one that changed since is <c>Skipped</c> <c>TooRecent</c> and stays where it is.
    /// </summary>
    /// <returns>The move, or the item's result when nothing was moved.</returns>
    internal (ExternalMoveResult? Moved, IngestItemResult? Stop) Place(IngestFile file, ExternalPlacement placement, string target, IngestItemResult result)
    {
        var source = placement.Source;
        IngestItemResult Io(string? details) => IngestItemResult.Error(IngestErrors.IoError, details, result.SystemId, result.Sha1);
        var tooRecent = new IngestItemResult(IngestOutcome.Skipped, IngestReason.TooRecent, SystemId: file.SystemId, Sha1: result.Sha1);

        pipeline.ExternalHooks?.BeforePlacement?.Invoke(file);
        var current = ExternalRomPath.ClassifyFile(source.Root, source.Path);
        if (current.Status == ExternalPathStatus.Found && current.Snapshot != source.Snapshot)
        {
            logger.LogInformation("{Path}: changed since it was hashed; left for a later run", file.FullPath);
            return (null, tooRecent);
        }
        if (current.Status != ExternalPathStatus.Found)
            return (null, Io(current.Details));
        var library = paths.Value.Roms;
        var folder = ExternalRomPath.ClassifyDirectory(library, Path.GetDirectoryName(target)!);
        if (folder.Status != ExternalPathStatus.Found)
            return (null, Io(folder.Details));

        ExternalMoveResult moved;
        try
        {
            moved = pipeline.IngestFileMoves.Tracked(source.Progress, JobStepPhases.CopyingToLibrary, source.Snapshot.Length, onCopied =>
                ExternalRomMove.Move(source.Root, current.Path, Path.Combine(folder.Path, placement.ExternalFileName), source.Snapshot,
                    logger, result.Sha1, pipeline.ExternalHooks?.Move, onCopied));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, Io(ex.Message));
        }
        switch (moved.Status)
        {
            case ExternalMoveStatus.Moved:
                return (moved, null);
            case ExternalMoveStatus.SourceChanged:
                logger.LogInformation("{Path}: changed while it was moved ({Details}); left for a later run", file.FullPath, moved.Details);
                return (null, tooRecent);
            default:
                var elsewhere = moved.SurvivingPath is { } survivor && survivor != current.Path;
                return (null, Io(elsewhere ? $"{moved.Details}; the complete copy is at {moved.SurvivingPath}" : moved.Details));
        }
    }

    /// <summary>
    /// I4/I5 after a failed ZIP write: a loose file goes back to its ingest path (or a free sibling name); an archive
    /// entry's copy is deleted only while the archive still holds those bytes, else it is kept as an orphan (beside the
    /// pointer, for a repair). A repair's original file then goes back to its own name, once the pointer is free.
    /// </summary>
    /// <returns>Where the bytes are, for the item's details; null when everything is back exactly where it was.</returns>
    /// <returns>
    /// Where the bytes are, for the item's details (null when everything is back exactly where it was), and the
    /// ingest path a loose source was fully restored under when its own name was taken.
    /// </returns>
    internal (string? Note, string? RestoredAs) RollBack(IngestFile file, ExternalPlacement placement, ExternalMoveResult moved, RepairPlan? repair)
    {
        var (note, pointerFree, restoredAs) = RemoveIncoming(file, placement, moved, repair);
        if (repair?.ExistingPath is null)
            return (note, restoredAs);
        if (!pointerFree)
        {
            var original = RepairOriginalNote(repair);
            logger.LogError("{Original}: the pointer is still occupied after rollback; {OriginalStatus}", repair.ExistingPath, original);
            return (IngestPipeline.Join(note, original), restoredAs);
        }
        return (IngestPipeline.Join(note, RestoreOriginal(repair)), restoredAs);
    }

    /// <returns>A note for the details, and whether the placed copy is gone from its library name.</returns>
    internal (string? Note, bool PointerFree, string? RestoredAs) RemoveIncoming(IngestFile file, ExternalPlacement placement, ExternalMoveResult moved, RepairPlan? repair)
    {
        var library = paths.Value.Roms;
        var placed = moved.Path!;
        if (file.ArchiveEntry is not null)
        {
            var archive = file.ArchiveSnapshot is null ? null : ExternalRomPath.ClassifyFile(paths.Value.Ingest, file.ArchivePath!);
            if (archive is not { Status: ExternalPathStatus.Found } || archive.Snapshot != file.ArchiveSnapshot)
            {
                if (repair is null)
                {
                    logger.LogError("{Archive} › {Entry}: the archive changed, so the verified copy is kept at {Orphan}", file.ArchivePath, file.ArchiveEntry, placed);
                    return ($"the archive changed, so the verified copy was kept at {placed}", false, null);
                }
                // I5: the pointer belongs to the original file, so the verified copy steps aside to an unlisted name.
                var aside = pipeline.IngestFileMoves.Tracked(placement.Source.Progress, JobStepPhases.Copying, moved.Snapshot!.Length, onCopied =>
                    ExternalRomMove.Move(library, placed, repair.OrphanPath, moved.Snapshot!, logger, hooks: pipeline.ExternalHooks?.Recovery, onCopied: onCopied));
                if (aside.Status != ExternalMoveStatus.Moved)
                {
                    var at = aside.SurvivingPath ?? placed;
                    logger.LogError("{Archive} › {Entry}: the archive changed and the verified copy couldn't be set aside ({Details}); it is at {At}",
                        file.ArchivePath, file.ArchiveEntry, aside.Details, at);
                    return ($"the archive changed and setting the verified copy aside failed ({aside.Details}); it is at {at}", false, null);
                }
                logger.LogError("{Archive} › {Entry}: the archive changed, so the verified copy is kept at {Orphan}", file.ArchivePath, file.ArchiveEntry, repair.OrphanPath);
                return ($"the archive changed, so the verified copy was kept at {repair.OrphanPath}", true, null);
            }
            if (ExternalRomPath.ClassifyFile(library, placed).Snapshot != moved.Snapshot)
            {
                logger.LogError("{Orphan} is no longer the copy that was placed; it was left as it is", placed);
                return ($"{placed} is no longer the copy that was placed; it was left as it is", false, null);
            }
            try
            {
                File.Delete(placed);
                return (null, true, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("{Orphan}: the copy couldn't be removed: {Error}", placed, ex.Message);
                return ($"the copy at {placed} couldn't be removed", false, null);
            }
        }

        var original = placement.Source.Path;
        var back = ExternalRomName.AllocateRollbackPath(original, fitSourcePathColumn: true);
        if (back is null)
        {
            logger.LogError("{Path}: no free name to move it back to; the complete copy is at {Placed}", original, placed);
            return ($"no free name to move it back to; the complete copy is at {placed}", false, null);
        }
        var restored = pipeline.IngestFileMoves.Tracked(placement.Source.Progress, JobStepPhases.Copying, moved.Snapshot!.Length, onCopied =>
            ExternalRomMove.Move(library, placed, back, moved.Snapshot!, logger, onCopied: onCopied));
        switch (restored.Status)
        {
            case ExternalMoveStatus.Moved when back == original:
                return (null, true, null);
            case ExternalMoveStatus.Moved:
                logger.LogWarning("{Path}: its name was taken, so it was moved back as {Restored}", original, back);
                return ($"moved back as {back}", true, back);
            case ExternalMoveStatus.SourceChanged:
                logger.LogError("{Placed} was replaced before it could be moved back; it was left as it is", placed);
                return ($"{placed} was replaced before it could be moved back; it was left as it is", false, null);
            default:
                // Never name a path as the copy unless the move vouches for it.
                var where = restored.SurvivingPath is { } survivor ? $"the complete copy is at {survivor}" : "the owned copy could not be found";
                logger.LogError("{Path}: moving it back failed ({Details}); {Where}", original, restored.Details, where);
                return ($"moving it back failed ({restored.Details}); {where}", false, null);
        }
    }

    /// <summary>I6: sets the file at the pointer aside under its reserved backup name.</summary>
    /// <returns>Why it wasn't and where the verified original remains; null when it was.</returns>
    internal string? StepAside(RepairPlan repair)
    {
        var library = paths.Value.Roms;
        var current = ExternalRomPath.ClassifyFile(library, repair.ExistingPath!);
        if (current.Status != ExternalPathStatus.Found || current.Snapshot != repair.Snapshot)
            return $"{repair.ExistingPath} changed before it could be repaired: {current.Details ?? "it was replaced"}";
        var backup = ExternalRomPath.ClassifyFile(library, repair.BackupPath);
        if (backup.Status != ExternalPathStatus.Missing)
            return IngestPipeline.Join($"{repair.BackupPath} can't hold the repair backup: {backup.Details ?? "the name is occupied"}", RepairOriginalNote(repair));
        var renamed = NoReplaceRename.MoveVerified(current.Path, repair.BackupPath, current.Snapshot!, pipeline.ExternalHooks?.RepairRename);
        if (!renamed.Renamed)
            return IngestPipeline.Join($"{repair.ExistingPath} couldn't be set aside as {repair.BackupPath}: {renamed.Details}", RepairOriginalNote(repair));
        return ExternalRomPath.ClassifyFile(library, repair.BackupPath).Snapshot == repair.Snapshot
            ? null
            : IngestPipeline.Join($"{repair.BackupPath} no longer holds the file set aside for repair", RepairOriginalNote(repair));
    }

    /// <summary>I6: puts the set-aside original back under its own spelling; nothing when there was none.</summary>
    /// <returns>A note when it couldn't be put back; null otherwise.</returns>
    internal string? RestoreOriginal(RepairPlan? repair)
    {
        if (repair?.ExistingPath is null)
            return null;
        var library = paths.Value.Roms;
        var backup = ExternalRomPath.ClassifyFile(library, repair.BackupPath);
        var original = ExternalRomPath.ClassifyFile(library, repair.ExistingPath);
        if (backup.Status == ExternalPathStatus.Missing && original.Snapshot == repair.Snapshot)
            return null; // it was never set aside
        if (backup.Status != ExternalPathStatus.Found || backup.Snapshot != repair.Snapshot)
        {
            logger.LogError("{Backup}: no longer the original file of {Original}; it was left as it is", repair.BackupPath, repair.ExistingPath);
            return $"{repair.BackupPath} is no longer the original file; it was left as it is";
        }
        string failure;
        if (original.Status == ExternalPathStatus.Missing)
        {
            var back = NoReplaceRename.MoveVerified(backup.Path, repair.ExistingPath, backup.Snapshot!, pipeline.ExternalHooks?.RepairRename);
            if (back.Renamed && ExternalRomPath.ClassifyFile(library, repair.ExistingPath).Snapshot == repair.Snapshot)
                return null;
            failure = back.Details ?? $"{repair.ExistingPath} no longer holds the restored file";
        }
        else
        {
            failure = original.Details ?? $"{repair.ExistingPath} is occupied";
        }
        var note = IngestPipeline.Join($"putting the original file back failed ({failure})", RepairOriginalNote(repair));
        logger.LogError("{Original}: {Details}", repair.ExistingPath, note);
        return note;
    }

    internal string? RepairOriginalNote(RepairPlan? repair)
    {
        if (repair?.ExistingPath is null)
            return null;
        var library = paths.Value.Roms;
        var backup = ExternalRomPath.ClassifyFile(library, repair.BackupPath);
        if (backup.Snapshot == repair.Snapshot)
            return $"the original file was kept at {backup.Path}";
        var original = ExternalRomPath.ClassifyFile(library, repair.ExistingPath);
        if (original.Snapshot == repair.Snapshot)
            return $"the original file was kept at {original.Path}";
        return "the original file could not be verified at either repair path";
    }

    /// <summary>I6 after the ZIP and database updates succeed: remove the verified backup.</summary>
    internal void DeleteBackup(RepairPlan repair)
    {
        if (repair.ExistingPath is null)
            return;
        try
        {
            if (ExternalRomPath.ClassifyFile(paths.Value.Roms, repair.BackupPath) is { Status: ExternalPathStatus.Found } backup && backup.Snapshot == repair.Snapshot)
                File.Delete(backup.Path);
            else
                logger.LogWarning("{Backup}: not the file the repair set aside; left as it is", repair.BackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Backup}: couldn't be removed after the repair ({Error}); integrity reports it", repair.BackupPath, ex.Message);
        }
    }

    internal static void AddImages(Dictionary<string, Func<Stream>> entries, MediaImage? boxArt, MediaImage? snap)
    {
        foreach (var image in new[] { boxArt, snap }.OfType<MediaImage>())
            entries[LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(image.Bytes);
    }
}
