using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using static RnRomManager.Core.Library.LibraryEditResult;

namespace RnRomManager.Core.Library;

/// <summary>The external-storage paths (W2–W5) of a release move and a merge: ROMs kept as files next to the game's ZIP.</summary>
internal sealed class ExternalReleaseEditor(
    LibraryEditSupport libraryEditSupport,
    IGameRepository games,
    IRomRepository roms,
    ILibraryMirror mirror,
    InfoJsonReader reader,
    ISystemRegistry systems,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    TimeProvider time,
    ILogger<LibraryEditor> logger)
{
    /// <summary>
    /// W3 for an external game. Both ZIPs are opened through checked handles and must be the mirrored games (I4). The
    /// release keeps an E3 logical name in the target and gets its E1 file name beside the target ZIP. A move that
    /// leaves releases in the source first publishes a verified backup of the source ZIP and its recovery record
    /// (12a–12b). Then: the file moves, the target ZIP is written, the source is rebuilt (or, for its last release,
    /// deleted after the DB update), and the DB is updated, each step only while the earlier publications still stand.
    /// Every failure before the source rebuild puts the file back; a failed rebuild first undoes the target.
    /// </summary>
    internal async Task<LibraryEditResult> MoveReleaseExternalAsync(
        GameRow row,
        RomRow romRow,
        GameRow? targetRow,
        string? newGameTitle,
        AuditActor actor)
    {
        var ct = CancellationToken.None; // edits write without the request's token (existing rule)
        var library = paths.Value.Roms;
        var source = LibraryTarget.OpenChecked(library, row.ZipPath, reader, row.Id, row.SystemId);
        if (source.Code is not null)
            return new(source.Code, source.Details);
        using var sourceZip = source.Zip!;
        if (source.Game!.Roms.FirstOrDefault(r => r.Metadata.Sha1 == romRow.Sha1) is not { ExternalFileName: { } sourcePointer } rom)
            return new(NotFound, $"{row.ZipPath} no longer lists SHA1 {romRow.Sha1}");
        var target = targetRow is null ? null : LibraryTarget.OpenChecked(library, targetRow.ZipPath, reader, targetRow.Id, targetRow.SystemId);
        if (target?.Code is not null)
            return new(target.Code, target.Details);
        using var targetZip = target?.Zip;

        // E3: the release keeps its name unless the target already has it; its note follows it.
        // B18: a FileSets name is never suffixed; a clash in the target game is NameTaken.
        var now = time.GetUtcNow();
        var fileSets = systems.ById(row.SystemId) is { FileSets: true };
        var targetNames = new ExternalNameReservations(target?.Game!.Roms.Select(r => r.FileName) ?? []);
        if (fileSets && targetNames.IsTaken(rom.FileName))
            return new(NameTaken, $"the target game already has a release named {rom.FileName}");
        var moved = fileSets ? rom : rom with { FileName = ExternalRomName.AllocateLogicalName(rom.FileName, targetNames) };
        var entries = new Dictionary<string, Func<Stream>>();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        LibraryEditSupport.CarryNote(sourceZip, rom.FileName, moved.FileName, entries, removed);

        var claims = await roms.ListExternalAsync(ct).ConfigureAwait(false);
        InfoJsonGame targetGame;
        string targetPath;
        if (target is null)
        {
            targetGame = await libraryEditSupport.NewGameForAsync(row.SystemId, rom, newGameTitle!, [], entries, now, ct).ConfigureAwait(false);
            targetPath =await ExternalRomName.AllocateZipPathAsync(library, row.SystemId, newGameTitle!, claims, games, ct).ConfigureAwait(false);
        }
        else
        {
            targetGame = target.Game!;
            targetPath = targetRow!.ZipPath;
        }

        // The target folder must be safe before anything is named or moved into it.
        var targetFull = SafePath.Resolve(library, targetPath);
        var folder = ExternalRomPath.EnsureDirectory(library, Path.GetDirectoryName(targetFull)!);
        if (folder.Status != ExternalPathStatus.Found)
            return new(IoError, folder.Details);

        var targetFolderPath = targetPath[..targetPath.LastIndexOf('/')];
        var sameFolder = string.Equals(targetFolderPath, row.ZipPath[..row.ZipPath.LastIndexOf('/')], StringComparison.OrdinalIgnoreCase);
        var reservations = ExternalNameReservations.ForFolder(folder.Path, targetFolderPath, claims, target?.Game!.ExternalFileNames());
        string pointer;
        if (fileSets)
        {
            // B18: every file keeps its exact name; within one folder nothing moves, elsewhere every name must be free.
            pointer = sourcePointer;
            if (!sameFolder && new[] { sourcePointer }.Concat((rom.Files ?? []).Select(f => f.ExternalFileName))
                    .FirstOrDefault(n => ExternalRomName.AllocateExact(n, reservations) != ExactNameStatus.Allocated) is { } taken)
                return new(NameTaken, $"{targetFolderPath}/{taken} is already used");
        }
        else
        {
            pointer = ExternalRomName.Allocate(Path.GetFileNameWithoutExtension(targetPath), moved.FileName, reservations);
        }
        moved = moved with { ExternalFileName = pointer };
        targetGame = targetGame with { Roms = PrimaryRelease.Apply([.. targetGame.Roms, moved], libraryEditSupport.DatSourceOf(row.SystemId), targetGame.ChosenPrimarySha1), Metadata = target is null ? targetGame.Metadata : libraryEditSupport.Touched(targetGame) };
        var remaining = source.Game.Roms.Where(r => !ReferenceEquals(r, rom)).ToList();
        var sourceGame = remaining.Count == 0 ? null : source.Game with { Roms = PrimaryRelease.Apply(remaining, libraryEditSupport.DatSourceOf(row.SystemId), source.Game.ChosenPrimarySha1), Metadata = libraryEditSupport.Touched(source.Game) };
        // B5: the last release deletes the source game, so its saves go with it into the target.
        var (savesError, saves) = sourceGame is null ? LibraryEditSupport.CarrySaves(sourceZip, targetZip, LibraryEditSupport.Renames((rom.FileName, moved.FileName))) : (null, null);
        if (savesError is not null)
            return savesError;

        var sourceFull = SafePath.Resolve(library, row.ZipPath);
        // B18: the main file, then each set file (none move within one folder); a missing one only moves its entry.
        var toMove = new List<(ExternalPathCheck File, string Name)>();
        var movePairs = fileSets && sameFolder ? []
            : new[] { (From: sourcePointer, To: pointer) }.Concat((rom.Files ?? []).Select(f => (From: f.ExternalFileName, To: f.ExternalFileName)));
        foreach (var (from, to) in movePairs)
        {
            var file = ExternalRomPath.ClassifyFile(library, Path.Combine(Path.GetDirectoryName(sourceZip.Path)!, from));
            if (file.Status is not (ExternalPathStatus.Found or ExternalPathStatus.Missing))
                return new(IoError, file.Details);
            if (file.Status == ExternalPathStatus.Missing)
                logger.LogWarning("Game {GameId}: {File} is missing, so only its pointer moves, to {Pointer}", row.Id, file.Path, to);
            else
                toMove.Add((file, to));
        }

        // W3: a multi-release source is backed up, with its recovery record, before any byte moves.
        ZipBackup? backup = null;
        ExternalFileSnapshot? recordSnapshot = null;
        if (sourceGame is not null)
        {
            try
            {
                backup = await ExternalZipBackup.CreateAsync(library, sourceFull, sourceZip.Snapshot!, libraryEditSupport.ExternalHooks?.Backup, ct).ConfigureAwait(false);
                IReadOnlyList<MoveRecordFile>? recordFiles = rom.Files?.Select(f => new MoveRecordFile(f.ExternalFileName, f.ExternalFileName)).ToList();
                recordSnapshot = await ExternalMoveRecord.WriteAsync(library, backup, new MoveRecord(MoveRecord.VersionFor(recordFiles), backup.Sha256, row.Id,
                    row.ZipPath, targetGame.GameId, targetPath, rom.Metadata.Sha1, sourcePointer, pointer, recordFiles), reader, libraryEditSupport.ExternalHooks?.Backup, ct)
                    .ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                logger.LogError(ex, "Moving a release out of {ZipPath}: the backup couldn't be made", row.ZipPath);
                RemoveBackup(backup, recordSnapshot);
                return new(IoError, ex.Message);
            }
        }

        var placed = new List<(string From, string To, ExternalFileSnapshot Snapshot)>();
        foreach (var (file, name) in toMove)
        {
            var move = ExternalRomMove.Move(library, file.Path, Path.Combine(folder.Path, name), file.Snapshot!, logger, hooks: libraryEditSupport.ExternalHooks?.Move);
            if (move.Status != ExternalMoveStatus.Moved)
            {
                libraryEditSupport.UndoMoves(placed);
                RemoveBackup(backup, recordSnapshot);
                return new(IoError, move.Details);
            }
            placed.Add((file.Path, move.Path!, move.Snapshot!));
        }
        var touched = placed.Count == 0 ? null
            : placed.ToDictionary(m => Path.GetFileName(m.To), m => m.Snapshot, StringComparer.OrdinalIgnoreCase);

        ExternalFileSnapshot publishedTarget;
        try
        {
            publishedTarget = await libraryEditSupport.Writer(targetGame.System).WriteCheckedAsync(library, targetFull, targetZip,
                new LibraryZipChanges(targetGame, entries, removed, touched, saves), ct, libraryEditSupport.ExternalHooks?.Writer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing {ZipPath} failed", targetPath);
            libraryEditSupport.UndoMoves(placed);
            RemoveBackup(backup, recordSnapshot);
            return LibraryEditSupport.WriteFailure(ex);
        }

        ExternalFileSnapshot? publishedSource = null;
        if (sourceGame is not null)
        {
            libraryEditSupport.ExternalHooks?.BeforeSourceRebuild?.Invoke();
            if (!libraryEditSupport.StillPublished(targetFull, publishedTarget))
            {
                // W3: the target is gone, so the untouched source ZIP, the backup and the moved file are all kept for W7.
                logger.LogError("Moving a release from {Source} to {Target}: the target was replaced before the source was rebuilt; " +
                    "kept the source, the backup {Backup} and the files at {Files}", row.ZipPath, targetPath, backup!.Path, string.Join(", ", placed.Select(m => m.To)));
                return new(DbWriteFailed, $"{targetPath} was replaced before {row.ZipPath} was rebuilt");
            }
            try
            {
                publishedSource = await libraryEditSupport.Writer(sourceGame.System).WriteCheckedAsync(library, sourceFull, sourceZip,
                    new LibraryZipChanges(sourceGame, new Dictionary<string, Func<Stream>>()), ct, libraryEditSupport.ExternalHooks?.Writer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rebuilding {ZipPath} failed", row.ZipPath);
                if (await UndoExternalTargetAsync(targetPath, target?.Game, publishedTarget).ConfigureAwait(false))
                    libraryEditSupport.UndoMoves(placed);
                else if (placed.Count > 0)
                    logger.LogError("The moved files stay at {Files}, beside {Target}", string.Join(", ", placed.Select(m => m.To)), targetPath);
                // The backup goes only when everything is as it was, including the source ZIP the move opened.
                if (libraryEditSupport.StillPublished(sourceFull, sourceZip.Snapshot!) && placed.All(m => ExternalRomPath.ClassifyFile(library, m.From).Snapshot == m.Snapshot))
                    RemoveBackup(backup, recordSnapshot);
                else
                    logger.LogError("Kept the backup {Backup} of {ZipPath} for recovery", backup!.Path, row.ZipPath);
                return LibraryEditSupport.WriteFailure(ex);
            }
        }

        audit.Record(new AuditEvent(AuditActions.ReleaseMoved, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
            AuditPaths.Library(targetPath), rom.FileName));
        libraryEditSupport.ExternalHooks?.BeforeMirror?.Invoke();
        var targetIntact = libraryEditSupport.StillPublished(targetFull, publishedTarget);
        var sourceIntact = publishedSource is null || libraryEditSupport.StillPublished(sourceFull, publishedSource);
        var result = targetIntact && sourceIntact
            ? await libraryEditSupport.MirrorAsync(() => mirror.ApplyAsync(
                [new MirrorEntry(targetGame, targetPath, publishedTarget.Length, publishedTarget.LastWriteTimeUtc),
                    .. sourceGame is null ? Array.Empty<MirrorEntry>() : [new MirrorEntry(sourceGame, row.ZipPath, publishedSource!.Length, publishedSource.LastWriteTimeUtc)]],
                sourceGame is null ? [row.Id] : [], ct), targetPath).ConfigureAwait(false)
            : new(DbWriteFailed, $"{(targetIntact ? row.ZipPath : targetPath)} was replaced after it was written");

        // W1/W3: cleanup only acts on files that are still what this move published or opened.
        targetIntact = libraryEditSupport.StillPublished(targetFull, publishedTarget);
        if (sourceGame is null)
        {
            sourceZip.Dispose(); // Windows can't delete an open file.
            if (targetIntact)
                libraryEditSupport.DeleteOldZipIfUnchanged(row.ZipPath, sourceZip.Snapshot!, row.Id);
            else
                logger.LogError("Kept {Source}: the target {Target} was replaced after it was written", row.ZipPath, targetPath);
        }
        else if (targetIntact && libraryEditSupport.StillPublished(sourceFull, publishedSource!))
        {
            RemoveBackup(backup, recordSnapshot);
        }
        else
        {
            logger.LogError("Kept the backup {Backup} of {ZipPath} and its record for recovery (W7)", backup!.Path, row.ZipPath);
        }

        if (result.Code is null)
        {
            logger.LogInformation("Release {FileName} moved from game {GameId} to {TargetId} ({ZipPath})", rom.FileName, row.Id, targetGame.GameId, targetPath);
            if (sourceGame is null)
                await libraryEditSupport.MoveCollectionMembershipsAsync(row.Id, targetGame.GameId).ConfigureAwait(false);
        }
        return result with { GameId = targetGame.GameId };
    }

    /// <summary>Removes a move's backup and its record, each only while it is still the file the move published.</summary>
    /// <param name="record">The published record; null when none was published, so whatever is at its name isn't the move's.</param>
    internal void RemoveBackup(ZipBackup? backup, ExternalFileSnapshot? record)
    {
        if (backup is null)
            return;
        foreach (var (path, owned) in new[] { (ExternalMoveRecord.PathOf(backup), record), (backup.Path, backup.Snapshot) })
        {
            try
            {
                if (owned is null)
                    continue;
                var current = ExternalRomPath.ClassifyFile(paths.Value.Roms, path);
                if (current.Status != ExternalPathStatus.Found)
                    continue;
                if (current.Snapshot != owned)
                {
                    logger.LogWarning("{Path} is no longer the file the move published; kept", path);
                    continue;
                }
                File.Delete(current.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("{Path} couldn't be removed: {Error}", path, ex.Message);
            }
        }
    }

    /// <summary>
    /// W3: a failed source rebuild after the target was published puts the target back: a new game's ZIP is deleted,
    /// an existing one rewritten to its earlier <c>info.json</c>. Either happens only while the target is still the
    /// ZIP this move published; a replacement is left alone.
    /// </summary>
    /// <returns>True when the target is back as it was.</returns>
    internal async Task<bool> UndoExternalTargetAsync(string targetPath, InfoJsonGame? before, ExternalFileSnapshot published)
    {
        var library = paths.Value.Roms;
        var path = SafePath.Resolve(library, targetPath);
        try
        {
            if (!libraryEditSupport.StillPublished(path, published))
            {
                logger.LogError("Couldn't undo the write of {ZipPath}: it was replaced; the replacement is kept", targetPath);
                return false;
            }
            if (before is null)
            {
                File.Delete(path);
                return true;
            }
            var opened = LibraryTarget.OpenChecked(library, targetPath, reader, before.GameId, before.System);
            if (opened.Code is not null || opened.Zip!.Snapshot != published)
            {
                opened.Zip?.Dispose();
                logger.LogError("Couldn't undo the write of {ZipPath}: {Details}", targetPath, opened.Details ?? "it changed");
                return false;
            }
            // The written target still has every entry it had before, so it can be rewritten from itself.
            await libraryEditSupport.Writer(before.System).WriteCheckedAsync(library, path, opened.Zip,
                new LibraryZipChanges(before, new Dictionary<string, Func<Stream>>()), CancellationToken.None, libraryEditSupport.ExternalHooks?.Writer).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't undo the write of {ZipPath}; the moved ROM is listed in two ZIPs until one is fixed by hand", targetPath);
            return false;
        }
    }

    /// <summary>
    /// W4 for an external game: both ZIPs are opened through checked handles (I4). Each release keeps an E3 logical
    /// name in the target (its note follows it) and its file is renamed to its E1 name beside the target ZIP before
    /// the target is written; a failed move or target write moves every file back. The source ZIP is deleted after
    /// the DB update only while the target is still the ZIP this merge published; otherwise it is kept and the result
    /// says <see cref="LibraryEditResult.TargetPublicationLost"/>.
    /// </summary>
    internal async Task<LibraryEditResult> MergeExternalAsync(GameRow row, GameRow targetRow, AuditActor actor)
    {
        var ct = CancellationToken.None; // edits write without the request's token (existing rule)
        var library = paths.Value.Roms;
        var source = LibraryTarget.OpenChecked(library, row.ZipPath, reader, row.Id, row.SystemId);
        if (source.Code is not null)
            return new(source.Code, source.Details);
        using var sourceZip = source.Zip!;
        var target = LibraryTarget.OpenChecked(library, targetRow.ZipPath, reader, targetRow.Id, targetRow.SystemId);
        if (target.Code is not null)
            return new(target.Code, target.Details);
        using var targetZip = target.Zip!;
        var (from, into) = (source.Game!, target.Game!);

        var entries = new Dictionary<string, Func<Stream>>();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        if (GameImages.Merge(into.Images, from.Images) is not var (images, copies))
            return new(TooManyImages, $"The merged game would have more than {GameImages.MaxImages} images.");
        foreach (var (copy, sourceName) in copies)
            entries[LibraryZip.ImageEntry(copy.FileName)] = () => sourceZip.OpenEntry(LibraryZip.ImageEntry(sourceName), copy.SizeBytes);

        var targetFull = SafePath.Resolve(library, targetRow.ZipPath);
        var folder = ExternalRomPath.ClassifyDirectory(library, Path.GetDirectoryName(targetFull)!);
        if (folder.Status != ExternalPathStatus.Found)
            return new(IoError, folder.Details);
        var claims = await roms.ListExternalAsync(ct).ConfigureAwait(false);
        var logical = new ExternalNameReservations(into.Roms.Select(r => r.FileName));
        var targetFolderPath = targetRow.ZipPath[..targetRow.ZipPath.LastIndexOf('/')];
        var pointers = ExternalNameReservations.ForFolder(folder.Path, targetFolderPath, claims, into.ExternalFileNames());
        var zipBase = Path.GetFileNameWithoutExtension(targetRow.ZipPath);
        var sourceFolder = Path.GetDirectoryName(sourceZip.Path)!;
        var merged = new List<InfoJsonRom>(into.Roms);
        var renames = LibraryEditSupport.Renames();
        var moves = new List<(string From, string To, ExternalFileSnapshot Snapshot)>();
        if (systems.ById(row.SystemId) is { FileSets: true })
        {
            // B18: every file keeps its exact name; within one folder nothing moves, elsewhere every name must be free.
            var sameFolder = string.Equals(targetFolderPath, row.ZipPath[..row.ZipPath.LastIndexOf('/')], StringComparison.OrdinalIgnoreCase);
            foreach (var rom in from.Roms)
            {
                if (logical.IsTaken(rom.FileName))
                    return new(NameTaken, $"the target game already has a release named {rom.FileName}");
                logical.Reserve(rom.FileName);
                merged.Add(rom);
                LibraryEditSupport.CarryNote(sourceZip, rom.FileName, rom.FileName, entries, removed);
                if (sameFolder)
                    continue;
                foreach (var name in new[] { rom.ExternalFileName! }.Concat((rom.Files ?? []).Select(f => f.ExternalFileName)))
                {
                    if (ExternalRomName.AllocateExact(name, pointers) != ExactNameStatus.Allocated)
                        return new(NameTaken, $"{targetFolderPath}/{name} is already used");
                    var file = ExternalRomPath.ClassifyFile(library, Path.Combine(sourceFolder, name));
                    if (file.Status == ExternalPathStatus.Missing)
                        logger.LogWarning("Game {GameId}: {File} is missing, so only its entry moves", row.Id, file.Path);
                    else if (file.Status != ExternalPathStatus.Found)
                        return new(IoError, file.Details);
                    else
                        moves.Add((file.Path, Path.Combine(folder.Path, name), file.Snapshot!));
                }
            }
        }
        else foreach (var rom in from.Roms)
        {
            var name = ExternalRomName.AllocateLogicalName(rom.FileName, logical);
            renames[rom.FileName] = name;
            var pointer = ExternalRomName.Allocate(zipBase, name, pointers);
            merged.Add(rom with { FileName = name, ExternalFileName = pointer });
            LibraryEditSupport.CarryNote(sourceZip, rom.FileName, name, entries, removed);
            var file = ExternalRomPath.ClassifyFile(library, Path.Combine(sourceFolder, rom.ExternalFileName!));
            if (file.Status == ExternalPathStatus.Missing)
                logger.LogWarning("Game {GameId}: {File} is missing, so only its pointer moves, to {Pointer}", row.Id, file.Path, pointer);
            else if (file.Status != ExternalPathStatus.Found)
                return new(IoError, file.Details);
            else
                moves.Add((file.Path, Path.Combine(folder.Path, pointer), file.Snapshot!));
        }
        var targetGame = libraryEditSupport.MergedInto(into, from, merged, images);
        // B5: refused before any file moves.
        var (savesError, saves) = LibraryEditSupport.CarrySaves(sourceZip, targetZip, renames);
        if (savesError is not null)
            return savesError;

        var placed = new List<(string From, string To, ExternalFileSnapshot Snapshot)>();
        var touched = new Dictionary<string, ExternalFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fromPath, toPath, snapshot) in moves)
        {
            var move = ExternalRomMove.Move(library, fromPath, toPath, snapshot, logger, hooks: libraryEditSupport.ExternalHooks?.Move);
            if (move.Status != ExternalMoveStatus.Moved)
            {
                libraryEditSupport.UndoMoves(placed);
                return new(IoError, move.Details);
            }
            placed.Add((fromPath, move.Path!, move.Snapshot!));
            touched[Path.GetFileName(move.Path!)] = move.Snapshot!;
        }

        ExternalFileSnapshot published;
        try
        {
            published = await libraryEditSupport.Writer(targetGame.System).WriteCheckedAsync(library, targetFull, targetZip,
                new LibraryZipChanges(targetGame, entries, removed, touched, saves), ct, libraryEditSupport.ExternalHooks?.Writer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing {ZipPath} failed", targetRow.ZipPath);
            libraryEditSupport.UndoMoves(placed);
            return LibraryEditSupport.WriteFailure(ex);
        }

        audit.Record(new AuditEvent(AuditActions.GamesMerged, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
            AuditPaths.Library(targetRow.ZipPath), $"{from.Roms.Count} releases, {copies.Count} images"));
        libraryEditSupport.ExternalHooks?.BeforeMirror?.Invoke();
        var result = libraryEditSupport.StillPublished(targetFull, published)
            ? await libraryEditSupport.MirrorAsync(() => mirror.ApplyAsync([new MirrorEntry(targetGame, targetRow.ZipPath, published.Length, published.LastWriteTimeUtc)],
                [row.Id], ct), targetRow.ZipPath).ConfigureAwait(false)
            : new(DbWriteFailed, $"{targetRow.ZipPath} was replaced after it was written");

        // W1/W4: the source goes only while the target is still the ZIP this merge published.
        sourceZip.Dispose(); // Windows can't delete an open file.
        if (libraryEditSupport.StillPublished(targetFull, published))
        {
            libraryEditSupport.DeleteOldZipIfUnchanged(row.ZipPath, sourceZip.Snapshot!, row.Id);
        }
        else
        {
            logger.LogError("Kept {Source}: the merge target {Target} was replaced after it was written", row.ZipPath, targetRow.ZipPath);
            result = result with { TargetPublicationLost = true };
        }
        if (result.Code is null)
        {
            logger.LogInformation("Game {GameId} merged into {TargetId} ({ZipPath})", row.Id, targetRow.Id, targetRow.ZipPath);
            await libraryEditSupport.MoveCollectionMembershipsAsync(row.Id, targetRow.Id).ConfigureAwait(false);
        }
        await libraryEditSupport.ClearIgdbSyncAsync(into, targetGame, result).ConfigureAwait(false);
        return result with { GameId = targetRow.Id };
    }
}
