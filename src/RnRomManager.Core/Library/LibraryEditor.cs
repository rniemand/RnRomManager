using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using static RnRomManager.Core.Library.LibraryEditResult;

namespace RnRomManager.Core.Library;

/// <summary>
/// Admin library edits (spec §11, §23, D28): metadata PATCH, delete release, delete game. Each takes the global job lock
/// (§3.1) and follows the sync rule (§14.2): the ZIP is written (or deleted) first, then the DB. Every write starts from
/// the ZIP's own <c>info.json</c> (§6.1). Removed ROM bytes go to <c>ingest/&lt;system&gt;/_removed/</c>, never deleted.
/// </summary>
public sealed class LibraryEditor(JobLock jobLock, IGameRepository games, IRomRepository roms, ILibraryMirror mirror,
    InfoJsonReader reader, IOptions<PathsOptions> paths, TimeProvider time, ILogger<LibraryEditor> logger)
{
    public async Task<LibraryEditResult> PatchAsync(Guid gameId, GamePatch patch, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        var target = LibraryTarget.Open(paths.Value.Roms, row.ZipPath, reader);
        if (target.Code is not null)
            return new(target.Code, target.Details);

        using var existing = target.Zip!;
        var current = target.Game!;
        var title = patch.Title ?? current.Title;
        var game = current with
        {
            Title = title,
            DisplayTitle = DisplayTitle.From(title),
            ReleaseYear = patch.SetReleaseYear ? patch.ReleaseYear : current.ReleaseYear,
            Developers = patch.Developers ?? current.Developers,
            Publishers = patch.Publishers ?? current.Publishers,
            Genres = patch.Genres ?? current.Genres,
            Description = patch.SetDescription ? patch.Description : current.Description,
            Metadata = Touched(current),
        };

        // §6.1: the ZIP name follows the title. Names compare case-insensitively (§5), so a case-only change keeps the
        // file where it is (and keeps any clash suffix it already has).
        var zipPath = row.ZipPath;
        var zipName = FileNameSanitizer.Sanitize(title) + ".zip";
        if (!string.Equals(FileNameSanitizer.Sanitize(current.Title) + ".zip", zipName, StringComparison.OrdinalIgnoreCase))
        {
            var folder = Path.Combine(paths.Value.Roms, game.System, Bucket.For(zipName));
            try
            {
                Directory.CreateDirectory(folder);
                zipName = FileNameRules.FirstFreeIn(folder, zipName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(IoError, ex.Message);
            }
            zipPath = $"{game.System}/{Bucket.For(zipName)}/{zipName}";
        }

        var result = await WriteAsync(existing, zipPath, game).ConfigureAwait(false);
        if (result.Code is null && zipPath != row.ZipPath)
            DeleteOldZip(row.ZipPath, gameId);
        if (result.Code is null)
            logger.LogInformation("Game {GameId} edited ({ZipPath})", gameId, zipPath);
        return result;
    }

    public async Task<LibraryEditResult> DeleteReleaseAsync(Guid gameId, long romId, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        if (await roms.GetAsync(gameId, romId, ct).ConfigureAwait(false) is not { } romRow
            || await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        var target = LibraryTarget.Open(paths.Value.Roms, row.ZipPath, reader);
        if (target.Code is not null)
            return new(target.Code, target.Details);

        using var existing = target.Zip!;
        var current = target.Game!;
        // The DB row only finds the release; the ZIP's info.json decides what's there (§6.1).
        if (current.Roms.FirstOrDefault(r => r.Metadata.Sha1 == romRow.Sha1) is not { } rom)
            return new(NotFound, $"{row.ZipPath} no longer lists SHA1 {romRow.Sha1}");

        var remaining = current.Roms.Where(r => !ReferenceEquals(r, rom)).ToList();
        var result = remaining.Count == 0
            ? await RemoveGameAsync(existing, row, current).ConfigureAwait(false)
            : await RemoveRomsAsync(existing, current, [rom], () => WriteAsync(existing, row.ZipPath,
                current with { Roms = PrimaryRelease.Apply(remaining), Metadata = Touched(current) })).ConfigureAwait(false);
        if (result.Code is null)
            logger.LogInformation("Release {FileName} removed from game {GameId} ({ZipPath})", rom.FileName, gameId, row.ZipPath);
        return result;
    }

    public async Task<LibraryEditResult> DeleteGameAsync(Guid gameId, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        var target = LibraryTarget.Open(paths.Value.Roms, row.ZipPath, reader);
        if (target.Code is not null)
            return new(target.Code, target.Details);

        using var existing = target.Zip!;
        var result = await RemoveGameAsync(existing, row, target.Game!).ConfigureAwait(false);
        if (result.Code is null)
            logger.LogInformation("Game {GameId} deleted ({ZipPath})", gameId, row.ZipPath);
        return result;
    }

    private Task<LibraryEditResult> RemoveGameAsync(LibraryZip existing, GameRow row, InfoJsonGame current) =>
        RemoveRomsAsync(existing, current, current.Roms, async () =>
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
            return await MirrorAsync(() => mirror.DeleteAsync(row.Id, CancellationToken.None), row.ZipPath).ConfigureAwait(false);
        });

    /// <summary>
    /// Copies <paramref name="removed"/> out of the ZIP into <c>_removed/</c>, then runs <paramref name="change"/> (the ZIP
    /// rewrite or delete + DB). The copies are taken back only when the ZIP itself didn't change.
    /// </summary>
    private async Task<LibraryEditResult> RemoveRomsAsync(LibraryZip existing, InfoJsonGame current, IReadOnlyList<InfoJsonRom> removed,
        Func<Task<LibraryEditResult>> change)
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
        return result;
    }

    /// <returns>null when every listed entry was copied (an absent entry has nothing to keep and is skipped).</returns>
    private LibraryEditResult? CopyToRemoved(LibraryZip zip, string systemId, IReadOnlyList<InfoJsonRom> removed, List<string> copies)
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

            byte[] bytes;
            try
            {
                using var stream = zip.OpenEntry(entry, rom.Metadata.SizeBytes);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            catch (Exception ex) when (ex is LimitExceededException or InvalidDataException)
            {
                logger.LogError(ex, "{Zip} › {Entry} can't be read; nothing was changed", zip.Path, entry);
                return new(VerifyFailed, $"{entry}: {ex.Message}");
            }
            if (Convert.ToHexStringLower(SHA1.HashData(bytes)) != rom.Metadata.Sha1)
                logger.LogWarning("{Zip} › {Entry} doesn't match its info.json SHA1; moved as it is", zip.Path, entry);

            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, FileNameRules.FirstFreeIn(folder, rom.FileName));
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    file.Write(bytes);
                copies.Add(path);
                logger.LogInformation("{Zip} › {Entry} moved to {Path}", zip.Path, entry, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Copying {Zip} › {Entry} to {Folder}/ failed", zip.Path, entry, StorageFolders.RemovedFolder);
                return new(IoError, ex.Message);
            }
        }
        return null;
    }

    /// <summary>Atomic ZIP write (§6.1) then the DB mirror. Not cancellable once started, so a ZIP is never left half-applied.</summary>
    private async Task<LibraryEditResult> WriteAsync(LibraryZip existing, string zipPath, InfoJsonGame game)
    {
        var target = SafePath.Resolve(paths.Value.Roms, zipPath);
        try
        {
            await new LibraryZipWriter(reader).WriteAsync(target, existing, new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>()))
                .ConfigureAwait(false);
        }
        catch (LibraryZipVerifyException ex)
        {
            logger.LogError(ex, "Verify failed writing {ZipPath}", zipPath);
            return new(VerifyFailed, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Writing {ZipPath} failed", zipPath);
            return new(IoError, ex.Message);
        }

        var info = new FileInfo(target);
        return await MirrorAsync(() => mirror.ReplaceAsync(new MirrorEntry(game, zipPath, info.Length, info.LastWriteTimeUtc), CancellationToken.None),
            zipPath).ConfigureAwait(false);
    }

    /// <summary>The ZIP already changed; a DB failure is healed later by the ingest pre-pass or integrity (§14.2).</summary>
    private async Task<LibraryEditResult> MirrorAsync(Func<Task> write, string zipPath)
    {
        try
        {
            await write().ConfigureAwait(false);
            return Ok;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ZipPath} changed but the DB update failed", zipPath);
            return new(DbWriteFailed, ex.Message);
        }
    }

    /// <summary>§6.1 title rename: the new ZIP is written and committed, then the old one goes.</summary>
    private void DeleteOldZip(string zipPath, Guid gameId)
    {
        try
        {
            File.Delete(SafePath.Resolve(paths.Value.Roms, zipPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Integrity reports both copies as DuplicateGameId (§12.2); deleting the old one by hand fixes it.
            logger.LogError(ex, "Game {GameId} was renamed but the old {ZipPath} couldn't be deleted", gameId, zipPath);
        }
    }

    private InfoJsonGameMetadata Touched(InfoJsonGame current) =>
        current.Metadata with { UpdatedAt = time.GetUtcNow(), AppVersion = AppInfo.Version };
}
