using Microsoft.Extensions.Logging;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Storage;
using static RnRomManager.Core.Library.LibraryEditResult;

namespace RnRomManager.Core.Library;

/// <summary>The external-storage paths (W2–W5): ROMs kept as files next to the game's ZIP.</summary>
public sealed partial class LibraryEditor
{
    /// <summary>
    /// W2 for an external game. The ZIP is opened through a checked handle and must be the mirrored game (I4). When
    /// the budgeted ZIP base changes (E2: a case-only change, or one past the byte cut, keeps it), the ZIP gets a new
    /// allocated path and every external file is moved to its E1 name beside it before the ZIP is written; a file that
    /// is already missing only gets its new name. Any failure moves the files back; after the DB update the old ZIP
    /// is removed, unless something else has taken its place.
    /// </summary>
    private async Task<LibraryEditResult> PatchExternalAsync(GameRow row, GamePatch patch, AuditActor? actor)
    {
        var ct = CancellationToken.None; // edits write without the request's token (existing rule)
        var library = paths.Value.Roms;
        var target = LibraryTarget.OpenChecked(library, row.ZipPath, reader, row.Id, row.SystemId);
        if (target.Code is not null)
            return new(target.Code, target.Details);
        using var existing = target.Zip!;
        var current = target.Game!;
        var game = Patched(current, patch);
        var oldFolder = Path.GetDirectoryName(existing.Path)!;

        var claims = await roms.ListExternalAsync(ct).ConfigureAwait(false);
        var oldFolderPath = row.ZipPath[..row.ZipPath.LastIndexOf('/')];
        // Moving a file another game also claims would take it from that game.
        if (current.ExternalFileNames().FirstOrDefault(name => claims.Any(c => c.GameId != row.Id
                && string.Equals(c.ExternalFileName, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.ZipPath[..c.ZipPath.LastIndexOf('/')], oldFolderPath, StringComparison.OrdinalIgnoreCase))) is { } conflict)
            return new(LibraryTarget.TargetZipUnreadable, $"{row.ZipPath}: {conflict} is also claimed by another game");

        var zipPath = row.ZipPath;
        var moves = new List<(string From, string To, ExternalFileSnapshot Snapshot)>();
        if (!ExternalRomName.SameZipBase(current.Title, game.Title))
        {
            zipPath = await ExternalRomName.AllocateZipPathAsync(library, game.System, game.Title, claims, games, ct, row.Id, row.ZipPath).ConfigureAwait(false);
            var newFolderPath = zipPath[..zipPath.LastIndexOf('/')];
            var newFolder = ExternalRomPath.EnsureDirectory(library, Path.GetDirectoryName(SafePath.Resolve(library, zipPath))!);
            if (newFolder.Status != ExternalPathStatus.Found)
                return new(IoError, newFolder.Details);

            var names = ExternalNameReservations.ForFolder(newFolder.Path, newFolderPath, claims.Where(c => c.GameId != row.Id));
            var zipBase = Path.GetFileNameWithoutExtension(zipPath);
            var renamed = new List<InfoJsonRom>();
            if (systems.ById(row.SystemId) is { FileSets: true })
            {
                // B16: names don't hold the ZIP base, so they never change; only a new letter folder moves every file.
                if (!string.Equals(newFolderPath, oldFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var name in current.ExternalFileNames())
                    {
                        if (ExternalRomName.AllocateExact(name, names) != ExactNameStatus.Allocated)
                            return new(NameTaken, $"{newFolderPath}/{name} is already used");
                        var file = ExternalRomPath.ClassifyFile(library, Path.Combine(oldFolder, name));
                        if (file.Status == ExternalPathStatus.Found)
                            moves.Add((file.Path, Path.Combine(newFolder.Path, name), file.Snapshot!));
                        else if (file.Status == ExternalPathStatus.Missing)
                            logger.LogWarning("Game {GameId}: {File} is missing, so it only keeps its name", row.Id, file.Path);
                        else
                            return new(IoError, file.Details);
                    }
                }
                renamed.AddRange(current.Roms);
            }
            else foreach (var rom in current.Roms)
            {
                var name = ExternalRomName.Allocate(zipBase, rom.FileName, names);
                var file = ExternalRomPath.ClassifyFile(library, Path.Combine(oldFolder, rom.ExternalFileName!));
                if (file.Status == ExternalPathStatus.Found)
                    moves.Add((file.Path, Path.Combine(newFolder.Path, name), file.Snapshot!));
                else if (file.Status == ExternalPathStatus.Missing)
                    logger.LogWarning("Game {GameId}: {File} is missing, so only its name changes, to {Name}", row.Id, file.Path, name);
                else
                    return new(IoError, file.Details);
                renamed.Add(rom with { ExternalFileName = name });
            }
            game = game with { Roms = renamed };
        }

        // W6: the moves come after every lookup and right before the first ZIP write.
        var placed = new List<(string From, string To, ExternalFileSnapshot Snapshot)>();
        foreach (var move in moves)
        {
            var moved = ExternalRomMove.Move(library, move.From, move.To, move.Snapshot, logger, hooks: ExternalHooks?.Move);
            if (moved.Status != ExternalMoveStatus.Moved)
            {
                UndoMoves(placed);
                return new(IoError, moved.Details);
            }
            placed.Add((move.From, moved.Path!, moved.Snapshot!));
        }

        ExternalFileSnapshot published;
        try
        {
            var touched = placed.ToDictionary(m => Path.GetFileName(m.To), m => m.Snapshot, StringComparer.OrdinalIgnoreCase);
            published = await Writer(game.System).WriteCheckedAsync(library, SafePath.Resolve(library, zipPath), existing,
                new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>(), ExternalFiles: touched), ct, ExternalHooks?.Writer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing {ZipPath} failed", zipPath);
            UndoMoves(placed);
            return WriteFailure(ex);
        }

        if (actor is not null && ChangedFields(current, game) is { Count: > 0 } changed)
            audit.Record(new AuditEvent(AuditActions.GameUpdated, actor, game.System, row.Id, AuditPaths.Library(row.ZipPath),
                AuditPaths.Library(zipPath), string.Join(", ", changed)));
        var result = await MirrorAsync(() =>
        {
            // W1: stamped from the published ZIP, and only while the path still names it.
            if (ExternalRomPath.ClassifyFile(library, SafePath.Resolve(library, zipPath)).Snapshot != published)
                throw new IOException($"{zipPath} is no longer the ZIP this edit published");
            return mirror.ReplaceAsync(new MirrorEntry(game, zipPath, published.Length, published.LastWriteTimeUtc), ct);
        }, zipPath).ConfigureAwait(false);
        if (result.Code is null && zipPath != row.ZipPath)
            DeleteOldZipIfUnchanged(row.ZipPath, existing.Snapshot!, row.Id);
        if (result.Code is null)
            logger.LogInformation("Game {GameId} edited ({ZipPath})", row.Id, zipPath);
        return result;
    }

    /// <summary>W6: moves the files of a failed edit back, newest first; a move that fails is logged and left.</summary>
    /// <param name="movedRoot">The root the files were moved under, when it isn't the library (<c>_removed/</c>).</param>
    private void UndoMoves(List<(string From, string To, ExternalFileSnapshot Snapshot)> placed, string? movedRoot = null)
    {
        for (var i = placed.Count - 1; i >= 0; i--)
        {
            var (from, to, snapshot) = placed[i];
            var back = ExternalRomMove.Move(movedRoot ?? paths.Value.Roms, to, from, snapshot, logger, hooks: ExternalHooks?.Move);
            if (back.Status != ExternalMoveStatus.Moved)
                logger.LogError("Moving {To} back to {From} failed ({Details}); integrity will report it", to, from, back.Details);
        }
    }

    /// <summary>W1: the renamed game's old ZIP goes only while it is still the file the edit opened.</summary>
    private void DeleteOldZipIfUnchanged(string zipPath, ExternalFileSnapshot opened, Guid gameId)
    {
        var path = SafePath.Resolve(paths.Value.Roms, zipPath);
        if (ExternalRomPath.ClassifyFile(paths.Value.Roms, path) is not { Status: ExternalPathStatus.Found } current || current.Snapshot != opened)
        {
            logger.LogError("Game {GameId} moved away from {ZipPath}, but what is there now isn't the ZIP it left; it was kept", gameId, zipPath);
            return;
        }
        DeleteOldZip(zipPath, gameId);
    }

    /// <summary>
    /// W3 for an external game. Both ZIPs are opened through checked handles and must be the mirrored games (I4). The
    /// release keeps an E3 logical name in the target and gets its E1 file name beside the target ZIP. A move that
    /// leaves releases in the source first publishes a verified backup of the source ZIP and its recovery record
    /// (12a–12b). Then: the file moves, the target ZIP is written, the source is rebuilt (or, for its last release,
    /// deleted after the DB update), and the DB is updated, each step only while the earlier publications still stand.
    /// Every failure before the source rebuild puts the file back; a failed rebuild first undoes the target.
    /// </summary>
    private async Task<LibraryEditResult> MoveReleaseExternalAsync(GameRow row, RomRow romRow, GameRow? targetRow, string? newGameTitle,
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
        CarryNote(sourceZip, rom.FileName, moved.FileName, entries, removed);

        var claims = await roms.ListExternalAsync(ct).ConfigureAwait(false);
        InfoJsonGame targetGame;
        string targetPath;
        if (target is null)
        {
            targetGame = await NewGameForAsync(row.SystemId, rom, newGameTitle!, [], entries, now, ct).ConfigureAwait(false);
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
        targetGame = targetGame with { Roms = PrimaryRelease.Apply([.. targetGame.Roms, moved], DatSourceOf(row.SystemId), targetGame.ChosenPrimarySha1), Metadata = target is null ? targetGame.Metadata : Touched(targetGame) };
        var remaining = source.Game.Roms.Where(r => !ReferenceEquals(r, rom)).ToList();
        var sourceGame = remaining.Count == 0 ? null : source.Game with { Roms = PrimaryRelease.Apply(remaining, DatSourceOf(row.SystemId), source.Game.ChosenPrimarySha1), Metadata = Touched(source.Game) };
        // B5: the last release deletes the source game, so its saves go with it into the target.
        var (savesError, saves) = sourceGame is null ? CarrySaves(sourceZip, targetZip, Renames((rom.FileName, moved.FileName))) : (null, null);
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
                backup = await ExternalZipBackup.CreateAsync(library, sourceFull, sourceZip.Snapshot!, ExternalHooks?.Backup, ct).ConfigureAwait(false);
                IReadOnlyList<MoveRecordFile>? recordFiles = rom.Files?.Select(f => new MoveRecordFile(f.ExternalFileName, f.ExternalFileName)).ToList();
                recordSnapshot = await ExternalMoveRecord.WriteAsync(library, backup, new MoveRecord(MoveRecord.VersionFor(recordFiles), backup.Sha256, row.Id,
                    row.ZipPath, targetGame.GameId, targetPath, rom.Metadata.Sha1, sourcePointer, pointer, recordFiles), reader, ExternalHooks?.Backup, ct)
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
            var move = ExternalRomMove.Move(library, file.Path, Path.Combine(folder.Path, name), file.Snapshot!, logger, hooks: ExternalHooks?.Move);
            if (move.Status != ExternalMoveStatus.Moved)
            {
                UndoMoves(placed);
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
            publishedTarget = await Writer(targetGame.System).WriteCheckedAsync(library, targetFull, targetZip,
                new LibraryZipChanges(targetGame, entries, removed, touched, saves), ct, ExternalHooks?.Writer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing {ZipPath} failed", targetPath);
            UndoMoves(placed);
            RemoveBackup(backup, recordSnapshot);
            return WriteFailure(ex);
        }

        ExternalFileSnapshot? publishedSource = null;
        if (sourceGame is not null)
        {
            ExternalHooks?.BeforeSourceRebuild?.Invoke();
            if (!StillPublished(targetFull, publishedTarget))
            {
                // W3: the target is gone, so the untouched source ZIP, the backup and the moved file are all kept for W7.
                logger.LogError("Moving a release from {Source} to {Target}: the target was replaced before the source was rebuilt; " +
                    "kept the source, the backup {Backup} and the files at {Files}", row.ZipPath, targetPath, backup!.Path, string.Join(", ", placed.Select(m => m.To)));
                return new(DbWriteFailed, $"{targetPath} was replaced before {row.ZipPath} was rebuilt");
            }
            try
            {
                publishedSource = await Writer(sourceGame.System).WriteCheckedAsync(library, sourceFull, sourceZip,
                    new LibraryZipChanges(sourceGame, new Dictionary<string, Func<Stream>>()), ct, ExternalHooks?.Writer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rebuilding {ZipPath} failed", row.ZipPath);
                if (await UndoExternalTargetAsync(targetPath, target?.Game, publishedTarget).ConfigureAwait(false))
                    UndoMoves(placed);
                else if (placed.Count > 0)
                    logger.LogError("The moved files stay at {Files}, beside {Target}", string.Join(", ", placed.Select(m => m.To)), targetPath);
                // The backup goes only when everything is as it was, including the source ZIP the move opened.
                if (StillPublished(sourceFull, sourceZip.Snapshot!) && placed.All(m => ExternalRomPath.ClassifyFile(library, m.From).Snapshot == m.Snapshot))
                    RemoveBackup(backup, recordSnapshot);
                else
                    logger.LogError("Kept the backup {Backup} of {ZipPath} for recovery", backup!.Path, row.ZipPath);
                return WriteFailure(ex);
            }
        }

        audit.Record(new AuditEvent(AuditActions.ReleaseMoved, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
            AuditPaths.Library(targetPath), rom.FileName));
        ExternalHooks?.BeforeMirror?.Invoke();
        var targetIntact = StillPublished(targetFull, publishedTarget);
        var sourceIntact = publishedSource is null || StillPublished(sourceFull, publishedSource);
        var result = targetIntact && sourceIntact
            ? await MirrorAsync(() => mirror.ApplyAsync(
                [new MirrorEntry(targetGame, targetPath, publishedTarget.Length, publishedTarget.LastWriteTimeUtc),
                    .. sourceGame is null ? Array.Empty<MirrorEntry>() : [new MirrorEntry(sourceGame, row.ZipPath, publishedSource!.Length, publishedSource.LastWriteTimeUtc)]],
                sourceGame is null ? [row.Id] : [], ct), targetPath).ConfigureAwait(false)
            : new(DbWriteFailed, $"{(targetIntact ? row.ZipPath : targetPath)} was replaced after it was written");

        // W1/W3: cleanup only acts on files that are still what this move published or opened.
        targetIntact = StillPublished(targetFull, publishedTarget);
        if (sourceGame is null)
        {
            sourceZip.Dispose(); // Windows can't delete an open file.
            if (targetIntact)
                DeleteOldZipIfUnchanged(row.ZipPath, sourceZip.Snapshot!, row.Id);
            else
                logger.LogError("Kept {Source}: the target {Target} was replaced after it was written", row.ZipPath, targetPath);
        }
        else if (targetIntact && StillPublished(sourceFull, publishedSource!))
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
                await MoveCollectionMembershipsAsync(row.Id, targetGame.GameId).ConfigureAwait(false);
        }
        return result with { GameId = targetGame.GameId };
    }

    private bool StillPublished(string fullPath, ExternalFileSnapshot published) =>
        ExternalRomPath.ClassifyFile(paths.Value.Roms, fullPath) is { Status: ExternalPathStatus.Found } current && current.Snapshot == published;

    /// <summary>Removes a move's backup and its record, each only while it is still the file the move published.</summary>
    /// <param name="record">The published record; null when none was published, so whatever is at its name isn't the move's.</param>
    private void RemoveBackup(ZipBackup? backup, ExternalFileSnapshot? record)
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
    private async Task<bool> UndoExternalTargetAsync(string targetPath, InfoJsonGame? before, ExternalFileSnapshot published)
    {
        var library = paths.Value.Roms;
        var path = SafePath.Resolve(library, targetPath);
        try
        {
            if (!StillPublished(path, published))
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
            await Writer(before.System).WriteCheckedAsync(library, path, opened.Zip,
                new LibraryZipChanges(before, new Dictionary<string, Func<Stream>>()), CancellationToken.None, ExternalHooks?.Writer).ConfigureAwait(false);
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
    private async Task<LibraryEditResult> MergeExternalAsync(GameRow row, GameRow targetRow, AuditActor actor)
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
        var renames = Renames();
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
                CarryNote(sourceZip, rom.FileName, rom.FileName, entries, removed);
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
            CarryNote(sourceZip, rom.FileName, name, entries, removed);
            var file = ExternalRomPath.ClassifyFile(library, Path.Combine(sourceFolder, rom.ExternalFileName!));
            if (file.Status == ExternalPathStatus.Missing)
                logger.LogWarning("Game {GameId}: {File} is missing, so only its pointer moves, to {Pointer}", row.Id, file.Path, pointer);
            else if (file.Status != ExternalPathStatus.Found)
                return new(IoError, file.Details);
            else
                moves.Add((file.Path, Path.Combine(folder.Path, pointer), file.Snapshot!));
        }
        var targetGame = MergedInto(into, from, merged, images);
        // B5: refused before any file moves.
        var (savesError, saves) = CarrySaves(sourceZip, targetZip, renames);
        if (savesError is not null)
            return savesError;

        var placed = new List<(string From, string To, ExternalFileSnapshot Snapshot)>();
        var touched = new Dictionary<string, ExternalFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fromPath, toPath, snapshot) in moves)
        {
            var move = ExternalRomMove.Move(library, fromPath, toPath, snapshot, logger, hooks: ExternalHooks?.Move);
            if (move.Status != ExternalMoveStatus.Moved)
            {
                UndoMoves(placed);
                return new(IoError, move.Details);
            }
            placed.Add((fromPath, move.Path!, move.Snapshot!));
            touched[Path.GetFileName(move.Path!)] = move.Snapshot!;
        }

        ExternalFileSnapshot published;
        try
        {
            published = await Writer(targetGame.System).WriteCheckedAsync(library, targetFull, targetZip,
                new LibraryZipChanges(targetGame, entries, removed, touched, saves), ct, ExternalHooks?.Writer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing {ZipPath} failed", targetRow.ZipPath);
            UndoMoves(placed);
            return WriteFailure(ex);
        }

        audit.Record(new AuditEvent(AuditActions.GamesMerged, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
            AuditPaths.Library(targetRow.ZipPath), $"{from.Roms.Count} releases, {copies.Count} images"));
        ExternalHooks?.BeforeMirror?.Invoke();
        var result = StillPublished(targetFull, published)
            ? await MirrorAsync(() => mirror.ApplyAsync([new MirrorEntry(targetGame, targetRow.ZipPath, published.Length, published.LastWriteTimeUtc)],
                [row.Id], ct), targetRow.ZipPath).ConfigureAwait(false)
            : new(DbWriteFailed, $"{targetRow.ZipPath} was replaced after it was written");

        // W1/W4: the source goes only while the target is still the ZIP this merge published.
        sourceZip.Dispose(); // Windows can't delete an open file.
        if (StillPublished(targetFull, published))
        {
            DeleteOldZipIfUnchanged(row.ZipPath, sourceZip.Snapshot!, row.Id);
        }
        else
        {
            logger.LogError("Kept {Source}: the merge target {Target} was replaced after it was written", row.ZipPath, targetRow.ZipPath);
            result = result with { TargetPublicationLost = true };
        }
        if (result.Code is null)
        {
            logger.LogInformation("Game {GameId} merged into {TargetId} ({ZipPath})", row.Id, targetRow.Id, targetRow.ZipPath);
            await MoveCollectionMembershipsAsync(row.Id, targetRow.Id).ConfigureAwait(false);
        }
        return result with { GameId = targetRow.Id };
    }

    /// <summary>
    /// W5 for an external game: each removed release's file moves to <c>ingest/&lt;system&gt;/_removed/&lt;fileName&gt;</c>
    /// (<c> (2)</c>… when the name or its staging name is taken) before the ZIP is rebuilt without it
    /// (<paramref name="rebuilt"/>) or, for the whole game, deleted. A failed write or deletion, or a ZIP that is no
    /// longer the one opened, moves the files back to their pointers; after a DB failure they stay in <c>_removed/</c>.
    /// A file that is already missing has nothing to move.
    /// </summary>
    private async Task<LibraryEditResult> RemoveExternalAsync(LibraryZip existing, GameRow row, IReadOnlyList<InfoJsonRom> removed,
        InfoJsonGame? rebuilt, AuditActor actor)
    {
        var ct = CancellationToken.None; // edits write without the request's token (existing rule)
        var (library, ingest) = (paths.Value.Roms, paths.Value.Ingest);
        var zipFull = SafePath.Resolve(library, row.ZipPath);
        // B17: every file of each release goes, the main file first, then its set files (by their own names).
        var files = new List<(InfoJsonRom Rom, string Name, bool IsMain, ExternalPathCheck File)>();
        foreach (var (rom, pointer, name, isMain) in removed.SelectMany(r => new[] { (r, r.ExternalFileName!, r.FileName, true) }
            .Concat((r.Files ?? []).Select(f => (r, f.ExternalFileName, f.FileName, false)))))
        {
            var file = ExternalRomPath.ClassifyFile(library, Path.Combine(Path.GetDirectoryName(existing.Path)!, pointer));
            if (file.Status == ExternalPathStatus.Missing)
                logger.LogWarning("Game {GameId}: {File} is missing; nothing to move to {Folder}/", row.Id, file.Path, StorageFolders.RemovedFolder);
            else if (file.Status != ExternalPathStatus.Found)
                return new(IoError, file.Details);
            else
                files.Add((rom, name, isMain, file));
        }

        var placed = new List<(string From, string To, ExternalFileSnapshot Snapshot)>();
        var copies = new Dictionary<InfoJsonRom, string>(ReferenceEqualityComparer.Instance);
        if (files.Count > 0)
        {
            var folder = ExternalRomPath.EnsureDirectory(ingest, Path.Combine(ingest, row.SystemId, StorageFolders.RemovedFolder));
            if (folder.Status != ExternalPathStatus.Found)
                return new(IoError, folder.Details);
            foreach (var (rom, name, isMain, file) in files)
            {
                var destination = ExternalRomName.AllocateSidePath(folder.Path, name);
                var move = destination is null
                    ? new ExternalMoveResult(ExternalMoveStatus.Failed, Details: $"no name for {name} fits in {folder.Path}")
                    : ExternalRomMove.Move(library, file.Path, destination, file.Snapshot!, logger, hooks: ExternalHooks?.Move);
                if (move.Status != ExternalMoveStatus.Moved)
                {
                    UndoMoves(placed, ingest);
                    return new(IoError, move.Details);
                }
                placed.Add((file.Path, move.Path!, move.Snapshot!));
                if (isMain)
                    copies[rom] = move.Path!;
                logger.LogInformation("{File} moved to {Path}", file.Path, move.Path);
            }
        }

        LibraryEditResult result;
        if (rebuilt is not null)
        {
            ExternalFileSnapshot published;
            try
            {
                published = await Writer(rebuilt.System).WriteCheckedAsync(library, zipFull, existing,
                    new LibraryZipChanges(rebuilt, new Dictionary<string, Func<Stream>>()), ct, ExternalHooks?.Writer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Writing {ZipPath} failed", row.ZipPath);
                UndoMoves(placed, ingest);
                return WriteFailure(ex);
            }
            var rom = removed[0];
            // B17: details name the main file, then each set file.
            audit.Record(new AuditEvent(AuditActions.ReleaseDeleted, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                copies.TryGetValue(rom, out var copy) ? auditPaths.Of(copy) : null,
                string.Join("; ", new[] { rom.FileName }.Concat((rom.Files ?? []).Select(f => f.FileName)))));
            ExternalHooks?.BeforeMirror?.Invoke();
            result = await MirrorAsync(() =>
            {
                // W1: stamped from the published ZIP, and only while the path still names it.
                if (!StillPublished(zipFull, published))
                    throw new IOException($"{row.ZipPath} is no longer the ZIP this edit published");
                return mirror.ReplaceAsync(new MirrorEntry(rebuilt, row.ZipPath, published.Length, published.LastWriteTimeUtc), ct);
            }, row.ZipPath).ConfigureAwait(false);
        }
        else
        {
            existing.Dispose(); // Windows can't delete an open file.
            try
            {
                if (!StillPublished(zipFull, existing.Snapshot!))
                    throw new IOException($"{row.ZipPath} is no longer the ZIP that was opened; it was kept");
                File.Delete(zipFull);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Deleting {ZipPath} failed", row.ZipPath);
                UndoMoves(placed, ingest);
                return new(IoError, ex.Message);
            }
            // Only the files went to _removed/; images and info.json were discarded.
            audit.Record(new AuditEvent(AuditActions.GameDeleted, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                $"ingest/{row.SystemId}/{StorageFolders.RemovedFolder}/"));
            result = await MirrorAsync(() => mirror.DeleteAsync(row.Id, ct), row.ZipPath).ConfigureAwait(false);
        }
        return result;
    }

}
