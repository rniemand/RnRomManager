using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using static RnRomManager.Core.Library.LibraryEditResult;

namespace RnRomManager.Core.Library;

/// <summary>
/// Admin library edits (spec §11, §23, D28, D35): metadata PATCH, delete release/game, move release, merge, retry media
/// (§10.2). Each takes the global job lock
/// (§3.1) and follows the sync rule (§14.2): the ZIP is written (or deleted) first, then the DB. Every write starts from
/// the ZIP's own <c>info.json</c> (§6.1). Removed ROM bytes go to <c>ingest/&lt;system&gt;/_removed/</c>, never deleted.
/// </summary>
public sealed class LibraryEditor(JobLock jobLock, IGameRepository games, IRomRepository roms, ILibraryMirror mirror,
    InfoJsonReader reader, DatCache dats, ISystemRegistry systems, MediaService media, GameMediaRetry mediaRetry,
    IOptions<PathsOptions> paths, TimeProvider time, ILogger<LibraryEditor> logger)
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
        if (!string.Equals(FileNameSanitizer.Sanitize(current.Title), FileNameSanitizer.Sanitize(title), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                zipPath = NewZipPath(game.System, title);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(IoError, ex.Message);
            }
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

    /// <summary>§10.2 "Retry media" for one game, with its own circuit breaker. Nothing new found is still a success.</summary>
    public async Task<LibraryEditResult> RetryMediaAsync(Guid gameId, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        var result = await mediaRetry.RetryAsync(row, new CircuitBreaker(), skipManualOnly: false, ct).ConfigureAwait(false);
        if (result.Outcome == MediaRetryOutcome.Error)
            logger.LogError("Media retry for {ZipPath} failed: {Code} {Details}", row.ZipPath, result.Code, result.Details);
        return result.Outcome == MediaRetryOutcome.Error ? new(result.Code, result.Details) : Ok;
    }

    /// <summary>
    /// §11 move release, to an existing game (<paramref name="targetGameId"/>) or a new one (<paramref name="newGameTitle"/>,
    /// which runs §7.2 steps 7–8 for it). The release keeps its <c>baseTitle</c> (D23). Order: target ZIP (now holding the
    /// ROM), source ZIP rebuilt, one DB transaction, then an emptied source ZIP is deleted.
    /// </summary>
    public async Task<LibraryEditResult> MoveReleaseAsync(Guid gameId, long romId, Guid? targetGameId, string? newGameTitle, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        if (targetGameId == gameId)
            return Invalid("targetGameId", "Choose a different game.");
        if (await roms.GetAsync(gameId, romId, ct).ConfigureAwait(false) is not { } romRow
            || await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        GameRow? targetRow = null;
        if (targetGameId is { } targetId)
        {
            targetRow = await games.GetAsync(targetId, ct).ConfigureAwait(false);
            if (targetRow is null)
                return new(NotFound);
            if (targetRow.SystemId != row.SystemId)
                return Invalid("targetGameId", "The target game must be in the same system.");
        }

        var source = LibraryTarget.Open(paths.Value.Roms, row.ZipPath, reader);
        if (source.Code is not null)
            return new(source.Code, source.Details);
        using var sourceZip = source.Zip!;
        if (source.Game!.Roms.FirstOrDefault(r => r.Metadata.Sha1 == romRow.Sha1) is not { } rom)
            return new(NotFound, $"{row.ZipPath} no longer lists SHA1 {romRow.Sha1}");
        var target = targetRow is null ? null : LibraryTarget.Open(paths.Value.Roms, targetRow.ZipPath, reader);
        if (target?.Code is not null)
            return new(target.Code, target.Details);
        using var targetZip = target?.Zip;

        var now = time.GetUtcNow();
        var moved = rom with { FileName = FreeName(rom.FileName, target?.Game!.Roms.Select(r => r.FileName) ?? []) };
        var entries = new Dictionary<string, Func<Stream>>
        {
            [LibraryZip.RomEntry(moved.FileName)] = () => sourceZip.OpenEntry(LibraryZip.RomEntry(rom.FileName), rom.Metadata.SizeBytes),
        };

        InfoJsonGame targetGame;
        string targetPath;
        if (target is null)
        {
            // §7.2 step 7 (all metadata from the release's DAT entry) and step 8 (media) for the new game.
            var handler = systems.ById(row.SystemId)!;
            var metadata = MetadataFor(row.SystemId, rom);
            var found = await media.FindAsync(handler, [rom.Identification.DatGameName], new CircuitBreaker(), ct).ConfigureAwait(false);
            targetGame = new InfoJsonGame
            {
                SchemaVersion = InfoJsonReader.CurrentVersion,
                GameId = Guid.NewGuid(),
                System = row.SystemId,
                Title = newGameTitle!,
                DisplayTitle = DisplayTitle.From(newGameTitle!),
                ReleaseYear = metadata.ReleaseYear,
                Developers = metadata.Developers,
                Publishers = metadata.Publishers,
                Genres = metadata.Genres,
                Description = null,
                Rating = null,
                Favourite = false,
                Roms = PrimaryRelease.Apply([moved]),
                Images = [found.BoxArt.ToInfoJson(now), .. found.Snap is { } snap ? [snap.ToInfoJson(now)] : Array.Empty<InfoJsonImage>()],
                Metadata = new InfoJsonGameMetadata(now, now, AppInfo.Version),
            };
            foreach (var image in new[] { found.BoxArt, found.Snap }.OfType<MediaImage>())
                entries[LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(image.Bytes);
            try
            {
                targetPath = NewZipPath(row.SystemId, newGameTitle!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(IoError, ex.Message);
            }
        }
        else
        {
            targetGame = target.Game! with { Roms = PrimaryRelease.Apply([.. target.Game.Roms, moved]), Metadata = Touched(target.Game) };
            targetPath = targetRow!.ZipPath;
        }

        var remaining = source.Game.Roms.Where(r => !ReferenceEquals(r, rom)).ToList();
        var sourceGame = remaining.Count == 0 ? null : source.Game with { Roms = PrimaryRelease.Apply(remaining), Metadata = Touched(source.Game) };

        if (await WriteZipAsync(targetZip, targetPath, targetGame, entries).ConfigureAwait(false) is { } failed)
            return failed;
        if (sourceGame is not null && await WriteZipAsync(sourceZip, row.ZipPath, sourceGame, []).ConfigureAwait(false) is { } sourceFailed)
        {
            await UndoTargetWriteAsync(targetPath, target?.Game).ConfigureAwait(false);
            return sourceFailed;
        }

        var result = await MirrorAsync(() => mirror.ApplyAsync(
            [Mirror(targetPath, targetGame), .. sourceGame is null ? Array.Empty<MirrorEntry>() : [Mirror(row.ZipPath, sourceGame)]],
            sourceGame is null ? [gameId] : [], CancellationToken.None), targetPath).ConfigureAwait(false);
        if (sourceGame is null)
        {
            sourceZip.Dispose(); // Windows can't delete an open file.
            DeleteOldZip(row.ZipPath, gameId);
        }
        if (result.Code is null)
            logger.LogInformation("Release {FileName} moved from game {GameId} to {TargetId} ({ZipPath})", rom.FileName, gameId, targetGame.GameId, targetPath);
        return result with { GameId = targetGame.GameId };
    }

    /// <summary>
    /// §11 Merge into… (D35): every release of the source moves into the target in one write. The target keeps its title,
    /// metadata and images; empty fields are filled from the source, a default box art is replaced by a real source one,
    /// and the source snap is used only when the target has none. Target ZIP + DB commit, then the source ZIP is deleted.
    /// </summary>
    public async Task<LibraryEditResult> MergeAsync(Guid gameId, Guid targetGameId, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        if (targetGameId == gameId)
            return Invalid("targetGameId", "Choose a different game.");
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row
            || await games.GetAsync(targetGameId, ct).ConfigureAwait(false) is not { } targetRow)
            return new(NotFound);
        if (targetRow.SystemId != row.SystemId)
            return Invalid("targetGameId", "The target game must be in the same system.");

        var source = LibraryTarget.Open(paths.Value.Roms, row.ZipPath, reader);
        if (source.Code is not null)
            return new(source.Code, source.Details);
        using var sourceZip = source.Zip!;
        var target = LibraryTarget.Open(paths.Value.Roms, targetRow.ZipPath, reader);
        if (target.Code is not null)
            return new(target.Code, target.Details);
        using var targetZip = target.Zip!;
        var (from, into) = (source.Game!, target.Game!);

        var entries = new Dictionary<string, Func<Stream>>();
        var names = into.Roms.Select(r => r.FileName).ToList();
        var merged = new List<InfoJsonRom>(into.Roms);
        foreach (var rom in from.Roms)
        {
            var moved = rom with { FileName = FreeName(rom.FileName, names) };
            names.Add(moved.FileName);
            merged.Add(moved);
            entries[LibraryZip.RomEntry(moved.FileName)] = () => sourceZip.OpenEntry(LibraryZip.RomEntry(rom.FileName), rom.Metadata.SizeBytes);
        }

        var images = into.Images.ToList();
        var intoBoxArt = into.Images.FirstOrDefault(i => i.Type == ImageType.BoxArt);
        if (from.Images.FirstOrDefault(i => i.Type == ImageType.BoxArt) is { IsDefault: false } boxArt && intoBoxArt is null or { IsDefault: true })
            TakeImage(boxArt, MediaImage.BoxArtFileName);
        if (from.Images.FirstOrDefault(i => i.Type == ImageType.Snap) is { } snap && !into.Images.Any(i => i.Type == ImageType.Snap))
            TakeImage(snap, MediaImage.SnapFileName);

        var targetGame = into with
        {
            ReleaseYear = into.ReleaseYear ?? from.ReleaseYear,
            Developers = into.Developers.Count > 0 ? into.Developers : from.Developers,
            Publishers = into.Publishers.Count > 0 ? into.Publishers : from.Publishers,
            Genres = into.Genres.Count > 0 ? into.Genres : from.Genres,
            Description = string.IsNullOrEmpty(into.Description) ? from.Description : into.Description,
            Roms = PrimaryRelease.Apply(merged),
            Images = images,
            Metadata = Touched(into),
        };

        if (await WriteZipAsync(targetZip, targetRow.ZipPath, targetGame, entries).ConfigureAwait(false) is { } failed)
            return failed;
        var result = await MirrorAsync(() => mirror.ApplyAsync([Mirror(targetRow.ZipPath, targetGame)], [gameId], CancellationToken.None),
            targetRow.ZipPath).ConfigureAwait(false);
        sourceZip.Dispose();
        DeleteOldZip(row.ZipPath, gameId);
        if (result.Code is null)
            logger.LogInformation("Game {GameId} merged into {TargetId} ({ZipPath})", gameId, targetGameId, targetRow.ZipPath);
        return result with { GameId = targetGameId };

        void TakeImage(InfoJsonImage image, string fileName)
        {
            images.RemoveAll(i => i.Type == image.Type || string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            images.Add(image with { FileName = fileName });
            entries[LibraryZip.ImageEntry(fileName)] = () => sourceZip.OpenEntry(LibraryZip.ImageEntry(image.FileName), image.SizeBytes);
        }
    }

    /// <summary>A failed source rebuild after the target was written: put the target back, so the ROM isn't in two ZIPs.</summary>
    private async Task UndoTargetWriteAsync(string targetPath, InfoJsonGame? before)
    {
        var path = SafePath.Resolve(paths.Value.Roms, targetPath);
        try
        {
            if (before is null)
            {
                File.Delete(path);
                return;
            }
            // The written target still has every entry it had before, so it can be rewritten from itself.
            await new LibraryZipWriter(reader).WriteAsync(path, LibraryZip.Open(path, reader),
                new LibraryZipChanges(before, new Dictionary<string, Func<Stream>>())).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't undo the write of {ZipPath}; the moved ROM is in two ZIPs until one is fixed by hand", targetPath);
        }
    }

    /// <summary>§7.2 step 7: metadat by the CRC of the DAT entry the release is identified with (Unverified: the chosen entry).</summary>
    private DatMetadata MetadataFor(string systemId, InfoJsonRom rom)
    {
        if (rom.Identification.DatGameName is not { } name || dats.For(systemId) is not { } store)
            return DatMetadata.Empty;
        var entry = rom.Identification.Method == IdentificationMethod.Dat
            ? store.BySha1(rom.Metadata.Sha1)
            : store.Roms.FirstOrDefault(r => r.GameName == name);
        return entry is null ? DatMetadata.Empty : store.MetadataFor(entry.Crc32);
    }

    /// <summary>§6.1 entry name clash: ` (2)`… before the extension, case-insensitively.</summary>
    private static string FreeName(string fileName, IEnumerable<string> taken) =>
        FileNameRules.FirstFree(fileName, taken.ToHashSet(StringComparer.OrdinalIgnoreCase).Contains);

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
    private async Task<LibraryEditResult> WriteAsync(LibraryZip existing, string zipPath, InfoJsonGame game) =>
        await WriteZipAsync(existing, zipPath, game, []).ConfigureAwait(false)
        ?? await MirrorAsync(() => mirror.ReplaceAsync(Mirror(zipPath, game), CancellationToken.None), zipPath).ConfigureAwait(false);

    /// <param name="existing">The game's current ZIP (disposed by the writer), or null for a new game.</param>
    /// <returns>null when written and verified.</returns>
    private async Task<LibraryEditResult?> WriteZipAsync(LibraryZip? existing, string zipPath, InfoJsonGame game,
        Dictionary<string, Func<Stream>> newEntries)
    {
        try
        {
            await new LibraryZipWriter(reader).WriteAsync(SafePath.Resolve(paths.Value.Roms, zipPath), existing,
                new LibraryZipChanges(game, newEntries)).ConfigureAwait(false);
            return null;
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
    }

    private MirrorEntry Mirror(string zipPath, InfoJsonGame game)
    {
        var info = new FileInfo(SafePath.Resolve(paths.Value.Roms, zipPath));
        return new MirrorEntry(game, zipPath, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>§6.1: <c>Sanitize(title).zip</c> in <c>roms/&lt;system&gt;/&lt;bucket&gt;/</c>, ` (2)`… on a clash (any case).</summary>
    private string NewZipPath(string systemId, string title)
    {
        var zipName = FileNameSanitizer.Sanitize(title) + ".zip";
        var folder = Path.Combine(paths.Value.Roms, systemId, Bucket.For(zipName));
        Directory.CreateDirectory(folder);
        zipName = FileNameRules.FirstFreeIn(folder, zipName);
        return $"{systemId}/{Bucket.For(zipName)}/{zipName}";
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

    /// <summary>
    /// After the DB commit: the old ZIP of a renamed game (§6.1), or the emptied source of a move/merge (§11). A leftover
    /// shows up in integrity as <c>DuplicateGameId</c> / <c>DuplicateSha1</c> (§12.2); deleting it by hand fixes it.
    /// </summary>
    private void DeleteOldZip(string zipPath, Guid gameId)
    {
        try
        {
            File.Delete(SafePath.Resolve(paths.Value.Roms, zipPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "The DB no longer points game {GameId} at {ZipPath}, but that ZIP couldn't be deleted", gameId, zipPath);
        }
    }

    private InfoJsonGameMetadata Touched(InfoJsonGame current) =>
        current.Metadata with { UpdatedAt = time.GetUtcNow(), AppVersion = AppInfo.Version };
}
