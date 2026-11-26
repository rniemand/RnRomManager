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

/// <summary>Removes a game, or the ROMs of a release, keeping removed ROM bytes in <c>ingest/&lt;system&gt;/_removed/</c>.</summary>
internal sealed class GameRemover(
    LibraryEditSupport libraryEditSupport,
    ILibraryMirror mirror,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    AuditPaths auditPaths,
    ILogger<LibraryEditor> logger)
{
    /// <summary>Deletes the game's ZIP (its ROMs copied to <c>_removed/</c> first) and records <c>GameDeleted</c>.</summary>
    internal async Task<LibraryEditResult> RemoveGameAsync(
        LibraryZip existing,
        GameRow row,
        InfoJsonGame current,
        AuditActor actor)
    {
        if (libraryEditSupport.IsExternal(row))
            return await RemoveExternalAsync(existing, row, current.Roms, null, actor).ConfigureAwait(false);
        var (result, _) = await RemoveRomsAsync(existing, current, current.Roms, async () =>
        {
            existing.Dispose(); // Windows can't delete an open file.
            try
            {
                File.Delete(SafePath.Resolve(paths.Value.Roms, row.ZipPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Deleting {ZipPath} failed", row.ZipPath);
                return new(IoError, ex.Message);
            }
            return await libraryEditSupport.MirrorAsync(() => mirror.DeleteAsync(row.Id, CancellationToken.None), row.ZipPath).ConfigureAwait(false);
        }).ConfigureAwait(false);
        // Only the ROM entries went to _removed/; images and info.json were discarded.
        if (LibraryEditSupport.FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.GameDeleted, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                $"ingest/{row.SystemId}/{StorageFolders.RemovedFolder}/"));
        return result;
    }

    /// <summary>
    /// Copies <paramref name="removed"/> out of the ZIP into <c>_removed/</c>, then runs <paramref name="change"/> (the ZIP
    /// rewrite or delete + DB). The copies are taken back only when the ZIP itself didn't change.
    /// </summary>
    /// <returns>The result, and the <c>_removed/</c> copies (after clash renaming; a missing entry has none).</returns>
    internal async Task<(LibraryEditResult Result, IReadOnlyList<string> Copies)> RemoveRomsAsync(LibraryZip existing, InfoJsonGame current,
        IReadOnlyList<InfoJsonRom> removed, Func<Task<LibraryEditResult>> change)
    {
        var copies = new List<string>();
        var result = CopyToRemoved(existing, current.System, removed, copies) ?? await change().ConfigureAwait(false);
        if (result.Code is not (null or DbWriteFailed))
        {
            foreach (var copy in copies)
            {
                try { File.Delete(copy); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Couldn't take back {Path} after a failed edit: {Error}", copy, ex.Message);
                }
            }
        }
        return (result, copies);
    }

    /// <returns>null when every listed entry was copied (an absent entry has nothing to keep and is skipped).</returns>
    internal LibraryEditResult? CopyToRemoved(
        LibraryZip zip,
        string systemId,
        IReadOnlyList<InfoJsonRom> removed,
        List<string> copies)
    {
        var folder = Path.Combine(paths.Value.Ingest, systemId, StorageFolders.RemovedFolder);
        foreach (var rom in removed)
        {
            var entry = LibraryZip.RomEntry(rom.FileName);
            if (!zip.HasEntry(entry))
            {
                logger.LogWarning("{Zip} › {Entry} is missing; nothing to move to {Folder}/", zip.Path, entry, StorageFolders.RemovedFolder);
                continue;
            }

            if (CopyEntryToRemoved(zip, entry, rom, folder) is { Failure: { } failure })
                return failure;
        }
        return null;

        LibraryEditResult ReadFailed(string entry, Exception ex)
        {
            logger.LogError(ex, "{Zip} › {Entry} can't be read; nothing was changed", zip.Path, entry);
            return new(VerifyFailed, $"{entry}: {ex.Message}");
        }

        LibraryEditResult WriteFailed(string entry, Exception ex)
        {
            logger.LogError(ex, "Copying {Zip} › {Entry} to {Folder}/ failed", zip.Path, entry, StorageFolders.RemovedFolder);
            return new(IoError, ex.Message);
        }

        // Streams one entry to a new _removed/ file, hashing as it goes, so a large release is never held in memory.
        // ZIP-side failures are VerifyFailed, destination failures IoError; either deletes the partial copy.
        (LibraryEditResult? Failure, string? Path) CopyEntryToRemoved(LibraryZip zip, string entry, InfoJsonRom rom, string folder)
        {
            Stream source;
            try { source = zip.OpenEntry(entry, rom.Metadata.SizeBytes); }
            catch (Exception ex) when (IsEntryReadFailure(ex)) { return (ReadFailed(entry, ex), null); }

            using (source)
            {
                string path;
                FileStream file;
                try
                {
                    Directory.CreateDirectory(folder);
                    path = Path.Combine(folder, FileNameRules.FirstFreeIn(folder, rom.FileName));
                    file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (WriteFailed(entry, ex), null); }

                LibraryEditResult? failure = null;
                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
                try
                {
                    while (failure is null)
                    {
                        int read;
                        try { read = source.Read(buffer, 0, CopyBufferBytes); }
                        catch (Exception ex) when (IsEntryReadFailure(ex)) { failure = ReadFailed(entry, ex); break; }
                        if (read == 0)
                            break;
                        sha1.AppendData(buffer, 0, read);
                        try { file.Write(buffer, 0, read); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = WriteFailed(entry, ex); }
                    }
                    try
                    {
                        if (failure is null)
                            file.Flush(flushToDisk: true); // on disk before the original is deleted
                        file.Dispose();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure ??= WriteFailed(entry, ex); }
                }
                finally
                {
                    file.Dispose();
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }

                if (failure is not null)
                {
                    try { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning("Couldn't delete the partial copy {Path}: {Error}", path, ex.Message);
                    }
                    return (failure, null);
                }
                if (Convert.ToHexStringLower(sha1.GetHashAndReset()) != rom.Metadata.Sha1)
                    logger.LogWarning("{Zip} › {Entry} doesn't match its info.json SHA1; moved as it is", zip.Path, entry);
                copies.Add(path);
                logger.LogInformation("{Zip} › {Entry} moved to {Path}", zip.Path, entry, path);
                return (null, path);
            }
        }
    }

    internal const int CopyBufferBytes = 1024 * 1024;

    /// <summary>A failure reading a ZIP entry: over its recorded size, corrupt or CRC mismatch, or an IO error from the entry stream.</summary>
    internal static bool IsEntryReadFailure(Exception ex) => ex is LimitExceededException or InvalidDataException or IOException;

    /// <summary>
    /// W5 for an external game: each removed release's file moves to <c>ingest/&lt;system&gt;/_removed/&lt;fileName&gt;</c>
    /// (<c> (2)</c>… when the name or its staging name is taken) before the ZIP is rebuilt without it
    /// (<paramref name="rebuilt"/>) or, for the whole game, deleted. A failed write or deletion, or a ZIP that is no
    /// longer the one opened, moves the files back to their pointers; after a DB failure they stay in <c>_removed/</c>.
    /// A file that is already missing has nothing to move.
    /// </summary>
    internal async Task<LibraryEditResult> RemoveExternalAsync(
        LibraryZip existing,
        GameRow row,
        IReadOnlyList<InfoJsonRom> removed,
        InfoJsonGame? rebuilt,
        AuditActor actor)
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
                    : ExternalRomMove.Move(library, file.Path, destination, file.Snapshot!, logger, hooks: libraryEditSupport.ExternalHooks?.Move);
                if (move.Status != ExternalMoveStatus.Moved)
                {
                    libraryEditSupport.UndoMoves(placed, ingest);
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
                published = await libraryEditSupport.Writer(rebuilt.System).WriteCheckedAsync(library, zipFull, existing,
                    new LibraryZipChanges(rebuilt, new Dictionary<string, Func<Stream>>()), ct, libraryEditSupport.ExternalHooks?.Writer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Writing {ZipPath} failed", row.ZipPath);
                libraryEditSupport.UndoMoves(placed, ingest);
                return LibraryEditSupport.WriteFailure(ex);
            }
            var rom = removed[0];
            // B17: details name the main file, then each set file.
            audit.Record(new AuditEvent(AuditActions.ReleaseDeleted, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                copies.TryGetValue(rom, out var copy) ? auditPaths.Of(copy) : null,
                string.Join("; ", new[] { rom.FileName }.Concat((rom.Files ?? []).Select(f => f.FileName)))));
            libraryEditSupport.ExternalHooks?.BeforeMirror?.Invoke();
            result = await libraryEditSupport.MirrorAsync(() =>
            {
                // W1: stamped from the published ZIP, and only while the path still names it.
                if (!libraryEditSupport.StillPublished(zipFull, published))
                    throw new IOException($"{row.ZipPath} is no longer the ZIP this edit published");
                return mirror.ReplaceAsync(new MirrorEntry(rebuilt, row.ZipPath, published.Length, published.LastWriteTimeUtc), ct);
            }, row.ZipPath).ConfigureAwait(false);
        }
        else
        {
            existing.Dispose(); // Windows can't delete an open file.
            try
            {
                if (!libraryEditSupport.StillPublished(zipFull, existing.Snapshot!))
                    throw new IOException($"{row.ZipPath} is no longer the ZIP that was opened; it was kept");
                File.Delete(zipFull);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Deleting {ZipPath} failed", row.ZipPath);
                libraryEditSupport.UndoMoves(placed, ingest);
                return new(IoError, ex.Message);
            }
            // Only the files went to _removed/; images and info.json were discarded.
            audit.Record(new AuditEvent(AuditActions.GameDeleted, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                $"ingest/{row.SystemId}/{StorageFolders.RemovedFolder}/"));
            result = await libraryEditSupport.MirrorAsync(() => mirror.DeleteAsync(row.Id, ct), row.ZipPath).ConfigureAwait(false);
        }
        return result;
    }
}
