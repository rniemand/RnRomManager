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

/// <summary>
/// Admin library edits (spec §11, §23, D28, D35): metadata PATCH, delete release/game, move release, merge, retry media
/// (§10.2). Each takes the global job lock
/// (§3.1) and follows the sync rule (§14.2): the ZIP is written (or deleted) first, then the DB. Every write starts from
/// the ZIP's own <c>info.json</c> (§6.1). Removed ROM bytes go to <c>ingest/&lt;system&gt;/_removed/</c>, never deleted.
/// </summary>
/// <summary>Test seams of <see cref="LibraryEditor"/>'s external edits.</summary>
public sealed class ExternalEditHooks
{
    /// <summary>Passed to every move of an edit, including the moves that undo it.</summary>
    public ExternalMoveHooks? Move { get; init; }

    /// <summary>Passed to every checked ZIP write of an edit, including a target's undo.</summary>
    public LibraryZipWriterHooks? Writer { get; init; }

    /// <summary>Passed to a move's source backup and its recovery record.</summary>
    public ExternalZipBackupHooks? Backup { get; init; }

    /// <summary>Runs after a move published its target, right before it checks that target and rebuilds the source.</summary>
    public Action? BeforeSourceRebuild { get; init; }

    /// <summary>Runs after a move wrote its ZIPs, right before it checks them and updates the DB.</summary>
    public Action? BeforeMirror { get; init; }
}

public sealed class LibraryEditor(JobLock jobLock, IGameRepository games, IRomRepository roms, IImageRepository images, ILibraryMirror mirror,
    InfoJsonReader reader, DatCache dats, ISystemRegistry systems, MediaService media, GameMediaRetry mediaRetry,
    IOptions<PathsOptions> paths, IAuditLog audit, AuditPaths auditPaths, ICollectionRepository collections, TimeProvider time,
    ILogger<LibraryEditor> logger)
{
    /// <summary>The ZIP was written (or deleted), so an audit row is due even when the DB mirror then failed (AU4).</summary>
    private static bool FilesChanged(LibraryEditResult result) => result.Code is null or DbWriteFailed;

    /// <param name="actor">The editing user; null (the rating/favourite endpoint) records nothing.</param>
    public async Task<LibraryEditResult> PatchAsync(Guid gameId, GamePatch patch, AuditActor? actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            return await PatchExternalAsync(row, patch, actor).ConfigureAwait(false);
        var target = LibraryTarget.Open(paths.Value.Roms, row.ZipPath, reader);
        if (target.Code is not null)
            return new(target.Code, target.Details);

        using var existing = target.Zip!;
        var current = target.Game!;
        var title = patch.Title ?? current.Title;
        var game = Patched(current, patch);

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

        // shortcut: a rating/favourite change rewrites the whole ZIP (fine for small GB/GBC ROMs); add a cheaper
        // info.json-only update path if larger systems make rating slow.
        var result = await WriteAsync(existing, zipPath, game).ConfigureAwait(false);
        if (actor is not null && FilesChanged(result) && ChangedFields(current, game) is { Count: > 0 } changed)
            audit.Record(new AuditEvent(AuditActions.GameUpdated, actor, game.System, gameId, AuditPaths.Library(row.ZipPath),
                AuditPaths.Library(zipPath), string.Join(", ", changed)));
        if (result.Code is null && zipPath != row.ZipPath)
            DeleteOldZip(row.ZipPath, gameId);
        if (result.Code is null)
            logger.LogInformation("Game {GameId} edited ({ZipPath})", gameId, zipPath);
        return result;
    }

    private InfoJsonGame Patched(InfoJsonGame current, GamePatch patch)
    {
        var title = patch.Title ?? current.Title;
        return current with
        {
            Title = title,
            DisplayTitle = DisplayTitle.From(title),
            ReleaseYear = patch.SetReleaseYear ? patch.ReleaseYear : current.ReleaseYear,
            Developers = patch.Developers ?? current.Developers,
            Publishers = patch.Publishers ?? current.Publishers,
            Genres = patch.Genres ?? current.Genres,
            Description = patch.SetDescription ? patch.Description : current.Description,
            Rating = patch.SetRating ? patch.Rating : current.Rating,
            Favourite = patch.Favourite ?? current.Favourite,
            Metadata = Touched(current),
        };
    }

    /// <summary>Test seams of the external edit path: points where a test makes a move or the ZIP write fail.</summary>
    public ExternalEditHooks? ExternalHooks { get; set; }

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
        if (current.Roms.Select(r => r.ExternalFileName).OfType<string>().FirstOrDefault(name => claims.Any(c => c.GameId != row.Id
                && string.Equals(c.ExternalFileName, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.ZipPath[..c.ZipPath.LastIndexOf('/')], oldFolderPath, StringComparison.OrdinalIgnoreCase))) is { } conflict)
            return new(LibraryTarget.TargetZipUnreadable, $"{row.ZipPath}: {conflict} is also claimed by another game");

        var zipPath = row.ZipPath;
        var moves = new List<(string From, string To, ExternalFileSnapshot Snapshot)>();
        if (!ExternalRomName.SameZipBase(current.Title, game.Title))
        {
            zipPath = await ExternalRomName.AllocateZipPathAsync(library, game.System, game.Title, claims, games, ct, row.Id, row.ZipPath).ConfigureAwait(false);
            var newFolderPath = zipPath[..zipPath.LastIndexOf('/')];
            var newFolder = ExternalRomPath.ClassifyDirectory(library, Path.GetDirectoryName(SafePath.Resolve(library, zipPath))!);
            try
            {
                if (newFolder.Status == ExternalPathStatus.Missing)
                {
                    Directory.CreateDirectory(newFolder.Path);
                    newFolder = ExternalRomPath.ClassifyDirectory(library, newFolder.Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(IoError, $"{newFolder.Path} can't be created: {ex.Message}");
            }
            if (newFolder.Status != ExternalPathStatus.Found)
                return new(IoError, newFolder.Details);

            var names = ExternalNameReservations.ForFolder(newFolder.Path, newFolderPath, claims.Where(c => c.GameId != row.Id));
            var zipBase = Path.GetFileNameWithoutExtension(zipPath);
            var renamed = new List<InfoJsonRom>();
            foreach (var rom in current.Roms)
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
            var moved = ExternalRomMove.Move(library, move.From, library, move.To, move.Snapshot, logger, hooks: ExternalHooks?.Move);
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
            published = await new LibraryZipWriter(reader).WriteCheckedAsync(library, SafePath.Resolve(library, zipPath), existing,
                new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>(), ExternalFiles: touched), ct, ExternalHooks?.Writer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing {ZipPath} failed", zipPath);
            UndoMoves(placed);
            if (ex is LibraryZipVerifyException)
                return new(VerifyFailed, ex.Message);
            if (ex is IOException or UnauthorizedAccessException)
                return new(IoError, ex.Message);
            throw;
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
    private void UndoMoves(List<(string From, string To, ExternalFileSnapshot Snapshot)> placed)
    {
        for (var i = placed.Count - 1; i >= 0; i--)
        {
            var (from, to, snapshot) = placed[i];
            var back = ExternalRomMove.Move(paths.Value.Roms, to, paths.Value.Roms, from, snapshot, logger, hooks: ExternalHooks?.Move);
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
    /// B5/B7: writes the release's note (already normalised, ≤ <see cref="LibraryZip.MaxNoteBytes"/>), or deletes it when
    /// <paramref name="text"/> is empty (no error when there was none). Any signed-in user; not audited; the game's fields,
    /// including <c>updatedAt</c>, stay as they are.
    /// </summary>
    public Task<LibraryEditResult> SaveNoteAsync(Guid gameId, long romId, string text, int? userId, CancellationToken ct) =>
        WriteNoteAsync(gameId, romId, text.Length == 0 ? null : System.Text.Encoding.UTF8.GetBytes(text), missingIsNotFound: false, userId, ct);

    /// <summary>B6: deletes the release's note; <c>NotFound</c> when there is none.</summary>
    public Task<LibraryEditResult> DeleteNoteAsync(Guid gameId, long romId, int? userId, CancellationToken ct) =>
        WriteNoteAsync(gameId, romId, null, missingIsNotFound: true, userId, ct);

    private async Task<LibraryEditResult> WriteNoteAsync(Guid gameId, long romId, byte[]? bytes, bool missingIsNotFound, int? userId,
        CancellationToken ct)
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
        if (current.Roms.FirstOrDefault(r => r.Metadata.Sha1 == romRow.Sha1) is not { } rom)
            return new(NotFound, $"{row.ZipPath} no longer lists SHA1 {romRow.Sha1}");
        var entry = LibraryZip.NoteEntry(rom.FileName);
        if (bytes is null && !existing.HasEntry(entry))
            return missingIsNotFound ? new(NotFound) : new();

        var entries = bytes is null ? [] : new Dictionary<string, Func<Stream>> { [entry] = () => new MemoryStream(bytes) };
        var removed = bytes is null ? new HashSet<string>(StringComparer.Ordinal) { entry } : null;
        var result = await WriteAsync(existing, row.ZipPath, current, entries, removed).ConfigureAwait(false);
        if (result.Code is null)
            logger.LogInformation("User {UserId} {Action} note {Entry} of game {GameId}", userId, bytes is null ? "deleted" : "saved", entry, gameId);
        return result;
    }

    public async Task<LibraryEditResult> DeleteReleaseAsync(Guid gameId, long romId, AuditActor actor, CancellationToken ct)
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
        if (remaining.Count == 0)
            return await RemoveGameAsync(existing, row, current, actor).ConfigureAwait(false); // the last release removes the game
        var (result, copies) = await RemoveRomsAsync(existing, current, [rom], () => WriteAsync(existing, row.ZipPath,
            current with { Roms = PrimaryRelease.Apply(remaining), Metadata = Touched(current) })).ConfigureAwait(false);
        if (FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.ReleaseDeleted, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
                copies.Count > 0 ? auditPaths.Of(copies[0]) : null, rom.FileName));
        if (result.Code is null)
            logger.LogInformation("Release {FileName} removed from game {GameId} ({ZipPath})", rom.FileName, gameId, row.ZipPath);
        return result;
    }

    public async Task<LibraryEditResult> DeleteGameAsync(Guid gameId, AuditActor actor, CancellationToken ct)
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
        var result = await RemoveGameAsync(existing, row, target.Game!, actor).ConfigureAwait(false);
        if (result.Code is null)
            logger.LogInformation("Game {GameId} deleted ({ZipPath})", gameId, row.ZipPath);
        return result;
    }

    /// <summary>
    /// I5: stores an uploaded PNG/JPEG as-is under <c>boxart-NN</c>/<c>snap-NN</c> (I2, extension from the magic bytes).
    /// A box art replaces the placeholder (and takes its cover status); the first image of an empty game is its cover (I3).
    /// U5: an image downloaded from a URL passes <paramref name="sourceUrl"/>; it is stored on the image and in the audit details.
    /// </summary>
    public async Task<LibraryEditResult> AddImageAsync(Guid gameId, ImageType type, byte[] bytes, AuditActor actor, CancellationToken ct,
        string? sourceUrl = null)
    {
        if (type is not (ImageType.BoxArt or ImageType.Snap))
            return Invalid("type", "Choose BoxArt or Snap.");
        if (bytes.Length == 0)
            return Invalid("file", "Choose an image file.");
        if (bytes.Length > ImageFormats.MaxBytes)
            return Invalid("file", "The image must be at most 8 MiB.");
        if (ImageFormats.Detect(bytes) is null)
            return Invalid("file", "The image must be a PNG or JPEG.");
        if (ImageFormats.Read(bytes) is not var (format, width, height))
            return Invalid("file", "The image's dimensions can't be read.");

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
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes));
        if (current.Images.Any(i => i.Sha1 == sha1))
            return new(DuplicateImage, "The game already has this image.");

        var list = current.Images.ToList();
        // I3: a box art replaces the placeholder (taking its cover status), so its name is free again.
        var placeholder = type == ImageType.BoxArt ? list.FirstOrDefault(i => i.IsDefault) : null;
        if (placeholder is not null)
            list.Remove(placeholder);
        var image = new InfoJsonImage
        {
            FileName = GameImages.NextName(list.Select(i => i.FileName), type, ImageFormats.Extension(format)),
            Type = type,
            Source = InfoJsonImage.SourceUpload,
            SourceUrl = sourceUrl,
            IsDefault = false,
            IsCover = placeholder?.IsCover ?? false,
            SizeBytes = bytes.Length,
            Sha1 = sha1,
            Width = width,
            Height = height,
            DateAdded = time.GetUtcNow(),
        };
        list.Add(image);
        if (list.Count > GameImages.MaxImages)
            return new(TooManyImages, $"A game can have at most {GameImages.MaxImages} images.");

        var game = current with { Images = GameImages.EnsureCover(list), Metadata = Touched(current) };
        var result = await WriteZipAsync(existing, row.ZipPath, game,
                new() { [LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(bytes) }).ConfigureAwait(false)
            ?? await MirrorAsync(() => mirror.ReplaceAsync(Mirror(row.ZipPath, game), CancellationToken.None), row.ZipPath).ConfigureAwait(false);
        if (FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.ImageAdded, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
                Details: sourceUrl is null ? image.FileName : $"{image.FileName} from {sourceUrl}"));
        if (result.Code is null)
            logger.LogInformation("Image {FileName} ({Type}) uploaded to game {GameId} ({ZipPath})", image.FileName, type, gameId, row.ZipPath);
        return result;
    }

    /// <summary>
    /// I6: removes an image; the placeholder can't be (<c>DefaultImage</c>). A removed cover passes to the first remaining
    /// <c>BoxArt</c> by file name, else the first remaining image, else the system's placeholder is added back as cover (I3).
    /// </summary>
    public async Task<LibraryEditResult> DeleteImageAsync(Guid gameId, long imageId, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        var (error, row, zip, current, image) = await OpenImageAsync(gameId, imageId, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        using var _ = zip;
        if (image!.IsDefault)
            return new(DefaultImage, "The placeholder is managed automatically; upload a box art to replace it.");

        var list = current!.Images.Where(i => !ReferenceEquals(i, image)).ToList();
        var entries = new Dictionary<string, Func<Stream>>();
        if (image.IsCover)
        {
            if (list.Where(i => i.Type == ImageType.BoxArt).OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } boxArt)
                list[list.IndexOf(boxArt)] = boxArt with { IsCover = true };
            else if (list.Count > 0)
                list[0] = list[0] with { IsCover = true };
            else if (systems.ById(current.System) is { } handler)
            {
                var placeholder = MediaService.Default(handler);
                list.Add(placeholder.ToInfoJson(time.GetUtcNow(), isCover: true));
                entries[LibraryZip.ImageEntry(placeholder.FileName)] = () => new MemoryStream(placeholder.Bytes);
            }
        }

        var game = current with { Images = list, Metadata = Touched(current) };
        var result = await WriteZipAsync(zip, row!.ZipPath, game, entries).ConfigureAwait(false)
            ?? await MirrorAsync(() => mirror.ReplaceAsync(Mirror(row.ZipPath, game), CancellationToken.None), row.ZipPath).ConfigureAwait(false);
        if (FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.ImageDeleted, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath), Details: image.FileName));
        if (result.Code is null)
            logger.LogInformation("Image {FileName} deleted from game {GameId} ({ZipPath})", image.FileName, gameId, row.ZipPath);
        return result;
    }

    /// <summary>I7: makes one image the cover (any type, the placeholder too).</summary>
    public async Task<LibraryEditResult> SetCoverAsync(Guid gameId, long imageId, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        var (error, row, zip, current, image) = await OpenImageAsync(gameId, imageId, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        using var _ = zip;
        if (image!.IsCover)
            return Ok;

        var game = current! with
        {
            Images = current.Images.Select(i => i with { IsCover = ReferenceEquals(i, image) }).ToList(),
            Metadata = Touched(current),
        };
        var result = await WriteAsync(zip!, row!.ZipPath, game).ConfigureAwait(false);
        if (FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.CoverSet, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath), Details: image.FileName));
        if (result.Code is null)
            logger.LogInformation("Image {FileName} is now the cover of game {GameId}", image.FileName, gameId);
        return result;
    }

    /// <summary>The game row, its open ZIP (the caller disposes it) and info.json, and the info.json image the DB row names.</summary>
    /// <returns>Error set (and nothing else) when the image or game isn't there or the ZIP can't be used.</returns>
    private async Task<(LibraryEditResult? Error, GameRow? Row, LibraryZip? Zip, InfoJsonGame? Game, InfoJsonImage? Image)> OpenImageAsync(
        Guid gameId, long imageId, CancellationToken ct)
    {
        if (await images.GetAsync(gameId, imageId, ct).ConfigureAwait(false) is not { } imageRow
            || await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return (new(NotFound), null, null, null, null);
        var target = LibraryTarget.Open(paths.Value.Roms, row.ZipPath, reader);
        if (target.Code is not null)
            return (new(target.Code, target.Details), null, null, null, null);
        // The DB row only finds the image; the ZIP's info.json decides what's there (§6.1).
        if (target.Game!.Images.FirstOrDefault(i => string.Equals(i.FileName, imageRow.FileName, StringComparison.OrdinalIgnoreCase)) is not { } image)
        {
            target.Zip!.Dispose();
            return (new(NotFound, $"{row.ZipPath} no longer lists {imageRow.FileName}"), null, null, null, null);
        }
        return (null, row, target.Zip, target.Game, image);
    }

    /// <summary>§10.2 "Retry media" for one game, with its own circuit breaker. Nothing new found is still a success.</summary>
    public async Task<LibraryEditResult> RetryMediaAsync(Guid gameId, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        var result = await mediaRetry.RetryAsync(row, new CircuitBreaker(), skipManualOnly: false, actor, ct).ConfigureAwait(false);
        if (result.Outcome == MediaRetryOutcome.Error)
            logger.LogError("Media retry for {ZipPath} failed: {Code} {Details}", row.ZipPath, result.Code, result.Details);
        return result.Outcome == MediaRetryOutcome.Error ? new(result.Code, result.Details) : Ok;
    }

    /// <summary>
    /// §11 move release, to an existing game (<paramref name="targetGameId"/>) or a new one (<paramref name="newGameTitle"/>,
    /// which runs §7.2 steps 7–8 for it). The release keeps its <c>baseTitle</c> (D23). Order: target ZIP (now holding the
    /// ROM), source ZIP rebuilt, one DB transaction, then an emptied source ZIP is deleted.
    /// </summary>
    public async Task<LibraryEditResult> MoveReleaseAsync(Guid gameId, long romId, Guid? targetGameId, string? newGameTitle, AuditActor actor,
        CancellationToken ct)
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
        if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            return await MoveReleaseExternalAsync(row, romRow, targetRow, newGameTitle, actor).ConfigureAwait(false);

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
        var removed = new HashSet<string>(StringComparer.Ordinal);
        CarryNote(sourceZip, rom.FileName, moved.FileName, entries, removed);

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
                Images = GameImages.ForNewGame(found, now),
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

        if (await WriteZipAsync(targetZip, targetPath, targetGame, entries, removed).ConfigureAwait(false) is { } failed)
            return failed;
        if (sourceGame is not null && await WriteZipAsync(sourceZip, row.ZipPath, sourceGame, []).ConfigureAwait(false) is { } sourceFailed)
        {
            await UndoTargetWriteAsync(targetPath, target?.Game).ConfigureAwait(false);
            return sourceFailed;
        }
        audit.Record(new AuditEvent(AuditActions.ReleaseMoved, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
            AuditPaths.Library(targetPath), rom.FileName));

        var result = await MirrorAsync(() => mirror.ApplyAsync(
            [Mirror(targetPath, targetGame), .. sourceGame is null ? Array.Empty<MirrorEntry>() : [Mirror(row.ZipPath, sourceGame)]],
            sourceGame is null ? [gameId] : [], CancellationToken.None), targetPath).ConfigureAwait(false);
        if (sourceGame is null)
        {
            sourceZip.Dispose(); // Windows can't delete an open file.
            DeleteOldZip(row.ZipPath, gameId);
        }
        if (result.Code is null)
        {
            logger.LogInformation("Release {FileName} moved from game {GameId} to {TargetId} ({ZipPath})", rom.FileName, gameId, targetGame.GameId, targetPath);
            if (sourceGame is null)
                await MoveCollectionMembershipsAsync(gameId, targetGame.GameId).ConfigureAwait(false);
        }
        return result with { GameId = targetGame.GameId };
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
        var now = time.GetUtcNow();
        var moved = rom with
        {
            FileName = ExternalRomName.AllocateLogicalName(rom.FileName, new ExternalNameReservations(target?.Game!.Roms.Select(r => r.FileName) ?? [])),
        };
        var entries = new Dictionary<string, Func<Stream>>();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        CarryNote(sourceZip, rom.FileName, moved.FileName, entries, removed);

        var claims = await roms.ListExternalAsync(ct).ConfigureAwait(false);
        InfoJsonGame targetGame;
        string targetPath;
        if (target is null)
        {
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
                Roms = [],
                Images = GameImages.ForNewGame(found, now),
                Metadata = new InfoJsonGameMetadata(now, now, AppInfo.Version),
            };
            foreach (var image in new[] { found.BoxArt, found.Snap }.OfType<MediaImage>())
                entries[LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(image.Bytes);
            targetPath = await ExternalRomName.AllocateZipPathAsync(library, row.SystemId, newGameTitle!, claims, games, ct).ConfigureAwait(false);
        }
        else
        {
            targetGame = target.Game!;
            targetPath = targetRow!.ZipPath;
        }

        // The target folder must be safe before anything is named or moved into it.
        var targetFull = SafePath.Resolve(library, targetPath);
        var folder = ExternalRomPath.ClassifyDirectory(library, Path.GetDirectoryName(targetFull)!);
        try
        {
            if (folder.Status == ExternalPathStatus.Missing)
            {
                Directory.CreateDirectory(folder.Path);
                folder = ExternalRomPath.ClassifyDirectory(library, folder.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(IoError, $"{folder.Path} can't be created: {ex.Message}");
        }
        if (folder.Status != ExternalPathStatus.Found)
            return new(IoError, folder.Details);

        var pointer = ExternalRomName.Allocate(Path.GetFileNameWithoutExtension(targetPath), moved.FileName,
            ExternalNameReservations.ForFolder(folder.Path, targetPath[..targetPath.LastIndexOf('/')], claims,
                target?.Game!.Roms.Select(r => r.ExternalFileName).OfType<string>()));
        moved = moved with { ExternalFileName = pointer };
        targetGame = targetGame with { Roms = PrimaryRelease.Apply([.. targetGame.Roms, moved]), Metadata = target is null ? targetGame.Metadata : Touched(targetGame) };
        var remaining = source.Game.Roms.Where(r => !ReferenceEquals(r, rom)).ToList();
        var sourceGame = remaining.Count == 0 ? null : source.Game with { Roms = PrimaryRelease.Apply(remaining), Metadata = Touched(source.Game) };

        var sourceFull = SafePath.Resolve(library, row.ZipPath);
        var file = ExternalRomPath.ClassifyFile(library, Path.Combine(Path.GetDirectoryName(sourceZip.Path)!, sourcePointer));
        if (file.Status is not (ExternalPathStatus.Found or ExternalPathStatus.Missing))
            return new(IoError, file.Details);
        if (file.Status == ExternalPathStatus.Missing)
            logger.LogWarning("Game {GameId}: {File} is missing, so only its pointer moves, to {Pointer}", row.Id, file.Path, pointer);

        // W3: a multi-release source is backed up, with its recovery record, before any byte moves.
        ZipBackup? backup = null;
        ExternalFileSnapshot? recordSnapshot = null;
        if (sourceGame is not null)
        {
            try
            {
                backup = await ExternalZipBackup.CreateAsync(library, sourceFull, sourceZip.Snapshot!, ExternalHooks?.Backup, ct).ConfigureAwait(false);
                recordSnapshot = await ExternalMoveRecord.WriteAsync(library, backup, new MoveRecord(MoveRecord.CurrentVersion, backup.Sha256, row.Id, row.ZipPath,
                    targetGame.GameId, targetPath, rom.Metadata.Sha1, sourcePointer, pointer), reader, ExternalHooks?.Backup, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                logger.LogError(ex, "Moving a release out of {ZipPath}: the backup couldn't be made", row.ZipPath);
                RemoveBackup(backup, recordSnapshot);
                return new(IoError, ex.Message);
            }
        }

        (string From, string To, ExternalFileSnapshot Snapshot)? placed = null;
        if (file.Status == ExternalPathStatus.Found)
        {
            var move = ExternalRomMove.Move(library, file.Path, library, Path.Combine(folder.Path, pointer), file.Snapshot!, logger, hooks: ExternalHooks?.Move);
            if (move.Status != ExternalMoveStatus.Moved)
            {
                RemoveBackup(backup, recordSnapshot);
                return new(IoError, move.Details);
            }
            placed = (file.Path, move.Path!, move.Snapshot!);
        }
        var touched = placed is { } p
            ? new Dictionary<string, ExternalFileSnapshot>(StringComparer.OrdinalIgnoreCase) { [pointer] = p.Snapshot }
            : null;

        ExternalFileSnapshot publishedTarget;
        try
        {
            publishedTarget = await new LibraryZipWriter(reader).WriteCheckedAsync(library, targetFull, targetZip,
                new LibraryZipChanges(targetGame, entries, removed, touched), ct, ExternalHooks?.Writer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing {ZipPath} failed", targetPath);
            UndoMoves(placed is { } back ? [back] : []);
            RemoveBackup(backup, recordSnapshot);
            if (ex is LibraryZipVerifyException)
                return new(VerifyFailed, ex.Message);
            if (ex is IOException or UnauthorizedAccessException)
                return new(IoError, ex.Message);
            throw;
        }

        ExternalFileSnapshot? publishedSource = null;
        if (sourceGame is not null)
        {
            ExternalHooks?.BeforeSourceRebuild?.Invoke();
            if (!StillPublished(targetFull, publishedTarget))
            {
                // W3: the target is gone, so the untouched source ZIP, the backup and the moved file are all kept for W7.
                logger.LogError("Moving a release from {Source} to {Target}: the target was replaced before the source was rebuilt; " +
                    "kept the source, the backup {Backup} and the file at {File}", row.ZipPath, targetPath, backup!.Path, placed?.To);
                return new(DbWriteFailed, $"{targetPath} was replaced before {row.ZipPath} was rebuilt");
            }
            try
            {
                publishedSource = await new LibraryZipWriter(reader).WriteCheckedAsync(library, sourceFull, sourceZip,
                    new LibraryZipChanges(sourceGame, new Dictionary<string, Func<Stream>>()), ct, ExternalHooks?.Writer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rebuilding {ZipPath} failed", row.ZipPath);
                if (await UndoExternalTargetAsync(targetPath, target?.Game, publishedTarget).ConfigureAwait(false))
                    UndoMoves(placed is { } back ? [back] : []);
                else if (placed is { } kept)
                    logger.LogError("The moved file stays at {File}, beside {Target}", kept.To, targetPath);
                // The backup goes only when everything is as it was, including the source ZIP the move opened.
                if (StillPublished(sourceFull, sourceZip.Snapshot!) && (placed is null || ExternalRomPath.ClassifyFile(library, placed.Value.From).Snapshot == placed.Value.Snapshot))
                    RemoveBackup(backup, recordSnapshot);
                else
                    logger.LogError("Kept the backup {Backup} of {ZipPath} for recovery", backup!.Path, row.ZipPath);
                if (ex is LibraryZipVerifyException)
                    return new(VerifyFailed, ex.Message);
                if (ex is IOException or UnauthorizedAccessException)
                    return new(IoError, ex.Message);
                throw;
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
            await new LibraryZipWriter(reader).WriteCheckedAsync(library, path, opened.Zip,
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
    /// §11 Merge into… (D35, I4a): every release of the source moves into the target in one write. The target keeps its
    /// title, metadata and cover; empty fields are filled from the source. The source's non-default images are copied in
    /// under new names (I2); its placeholder is dropped; a real source box art removes the target's placeholder (and becomes
    /// the cover if the placeholder was). More than <see cref="GameImages.MaxImages"/> images → <c>TooManyImages</c>.
    /// Target ZIP + DB commit, then the source ZIP is deleted.
    /// </summary>
    public async Task<LibraryEditResult> MergeAsync(Guid gameId, Guid targetGameId, AuditActor actor, CancellationToken ct)
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
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var names = into.Roms.Select(r => r.FileName).ToList();
        var merged = new List<InfoJsonRom>(into.Roms);
        foreach (var rom in from.Roms)
        {
            var moved = rom with { FileName = FreeName(rom.FileName, names) };
            names.Add(moved.FileName);
            merged.Add(moved);
            entries[LibraryZip.RomEntry(moved.FileName)] = () => sourceZip.OpenEntry(LibraryZip.RomEntry(rom.FileName), rom.Metadata.SizeBytes);
            CarryNote(sourceZip, rom.FileName, moved.FileName, entries, removed);
        }

        if (GameImages.Merge(into.Images, from.Images) is not var (images, copies))
            return new(TooManyImages, $"The merged game would have more than {GameImages.MaxImages} images.");
        foreach (var (copy, sourceName) in copies)
            entries[LibraryZip.ImageEntry(copy.FileName)] = () => sourceZip.OpenEntry(LibraryZip.ImageEntry(sourceName), copy.SizeBytes);

        var targetGame = into with
        {
            ReleaseYear = into.ReleaseYear ?? from.ReleaseYear,
            Developers = into.Developers.Count > 0 ? into.Developers : from.Developers,
            Publishers = into.Publishers.Count > 0 ? into.Publishers : from.Publishers,
            Genres = into.Genres.Count > 0 ? into.Genres : from.Genres,
            Description = string.IsNullOrEmpty(into.Description) ? from.Description : into.Description,
            Rating = into.Rating ?? from.Rating,
            Favourite = into.Favourite || from.Favourite,
            Roms = PrimaryRelease.Apply(merged),
            Images = images,
            Metadata = Touched(into),
        };

        if (await WriteZipAsync(targetZip, targetRow.ZipPath, targetGame, entries, removed).ConfigureAwait(false) is { } failed)
            return failed;
        audit.Record(new AuditEvent(AuditActions.GamesMerged, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
            AuditPaths.Library(targetRow.ZipPath), $"{from.Roms.Count} releases, {copies.Count} images"));
        var result = await MirrorAsync(() => mirror.ApplyAsync([Mirror(targetRow.ZipPath, targetGame)], [gameId], CancellationToken.None),
            targetRow.ZipPath).ConfigureAwait(false);
        sourceZip.Dispose();
        DeleteOldZip(row.ZipPath, gameId);
        if (result.Code is null)
        {
            logger.LogInformation("Game {GameId} merged into {TargetId} ({ZipPath})", gameId, targetGameId, targetRow.ZipPath);
            await MoveCollectionMembershipsAsync(gameId, targetGameId).ConfigureAwait(false);
        }
        return result with { GameId = targetGameId };
    }

    /// <summary>
    /// B18: the deleted source game's collection memberships follow it to the target. The library change has already
    /// committed, so a failure is only logged.
    /// </summary>
    private async Task MoveCollectionMembershipsAsync(Guid fromGameId, Guid toGameId)
    {
        try
        {
            await collections.MoveGameAsync(fromGameId, toGameId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't move the collection memberships of game {GameId} to {TargetId}", fromGameId, toGameId);
        }
    }

    /// <summary>
    /// B9: a release arriving under <paramref name="toName"/> brings its note from <paramref name="from"/>; without one it must
    /// not adopt an orphan note of that name already in the target.
    /// </summary>
    private static void CarryNote(LibraryZip from, string fromName, string toName, Dictionary<string, Func<Stream>> entries, HashSet<string> removed)
    {
        var (note, target) = (LibraryZip.NoteEntry(fromName), LibraryZip.NoteEntry(toName));
        if (from.HasEntry(note))
            entries[target] = () => from.OpenEntry(note, LibraryZip.MaxNoteBytes);
        else
            removed.Add(target);
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

    /// <summary>Deletes the game's ZIP (its ROMs copied to <c>_removed/</c> first) and records <c>GameDeleted</c>.</summary>
    private async Task<LibraryEditResult> RemoveGameAsync(LibraryZip existing, GameRow row, InfoJsonGame current, AuditActor actor)
    {
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
            return await MirrorAsync(() => mirror.DeleteAsync(row.Id, CancellationToken.None), row.ZipPath).ConfigureAwait(false);
        }).ConfigureAwait(false);
        // Only the ROM entries went to _removed/; images and info.json were discarded.
        if (FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.GameDeleted, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                $"ingest/{row.SystemId}/{StorageFolders.RemovedFolder}/"));
        return result;
    }

    /// <summary>
    /// Copies <paramref name="removed"/> out of the ZIP into <c>_removed/</c>, then runs <paramref name="change"/> (the ZIP
    /// rewrite or delete + DB). The copies are taken back only when the ZIP itself didn't change.
    /// </summary>
    /// <returns>The result, and the <c>_removed/</c> copies (after clash renaming; a missing entry has none).</returns>
    private async Task<(LibraryEditResult Result, IReadOnlyList<string> Copies)> RemoveRomsAsync(LibraryZip existing, InfoJsonGame current,
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
    private async Task<LibraryEditResult> WriteAsync(LibraryZip existing, string zipPath, InfoJsonGame game,
        Dictionary<string, Func<Stream>>? newEntries = null, IReadOnlySet<string>? removedEntries = null) =>
        await WriteZipAsync(existing, zipPath, game, newEntries ?? [], removedEntries).ConfigureAwait(false)
        ?? await MirrorAsync(() => mirror.ReplaceAsync(Mirror(zipPath, game), CancellationToken.None), zipPath).ConfigureAwait(false);

    /// <param name="existing">The game's current ZIP (disposed by the writer), or null for a new game.</param>
    /// <returns>null when written and verified.</returns>
    private async Task<LibraryEditResult?> WriteZipAsync(LibraryZip? existing, string zipPath, InfoJsonGame game,
        Dictionary<string, Func<Stream>> newEntries, IReadOnlySet<string>? removedEntries = null)
    {
        try
        {
            await new LibraryZipWriter(reader).WriteAsync(SafePath.Resolve(paths.Value.Roms, zipPath), existing,
                new LibraryZipChanges(game, newEntries, removedEntries)).ConfigureAwait(false);
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

    /// <summary>AU4 <c>GameUpdated</c> details: the metadata fields a PATCH changed (rating/favourite aren't metadata).</summary>
    private static List<string> ChangedFields(InfoJsonGame before, InfoJsonGame after)
    {
        var changed = new List<string>();
        if (before.Title != after.Title) changed.Add("title");
        if (before.ReleaseYear != after.ReleaseYear) changed.Add("releaseYear");
        if (!before.Developers.SequenceEqual(after.Developers)) changed.Add("developers");
        if (!before.Publishers.SequenceEqual(after.Publishers)) changed.Add("publishers");
        if (!before.Genres.SequenceEqual(after.Genres)) changed.Add("genres");
        if (before.Description != after.Description) changed.Add("description");
        return changed;
    }

    private InfoJsonGameMetadata Touched(InfoJsonGame current) =>
        current.Metadata with { UpdatedAt = time.GetUtcNow(), AppVersion = AppInfo.Version };
}
