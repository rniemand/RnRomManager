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
/// External-storage paths (W2–W5): the patch is here; moves and merges are in <see cref="ExternalReleaseEditor"/>.
/// </summary>
public sealed class LibraryEditor(
    JobLock jobLock,
    IGameRepository games,
    IRomRepository roms,
    IImageRepository images,
    ILibraryMirror mirror,
    InfoJsonReader reader,
    DatCache dats,
    ISystemRegistry systems,
    MediaService media,
    GameMediaRetry mediaRetry,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    AuditPaths auditPaths,
    ICollectionRepository collections,
    TimeProvider time,
    ILogger<LibraryEditor> logger,
    IRomStorageSettings? zipSettings = null)
{

    internal LibraryEditSupport LibraryEditSupport => field ??= new(games, mirror, reader, dats, systems, media, paths, collections, time, logger, zipSettings);

    internal ImageEditor ImageEditor => field ??= new(LibraryEditSupport, jobLock, games, images, mirror, systems, mediaRetry, audit, time, logger);

    internal SaveEditor SaveEditor => field ??= new(LibraryEditSupport, jobLock, games, roms, audit, time, logger);

    internal GameRemover GameRemover => field ??= new(LibraryEditSupport, mirror, paths, audit, auditPaths, logger);

    internal ExternalReleaseEditor ExternalReleaseEditor => field ??= new(LibraryEditSupport, games, roms, mirror, reader, systems, paths, audit, time, logger);

    internal ReleaseEditor ReleaseEditor => field ??= new(LibraryEditSupport, GameRemover, ExternalReleaseEditor, jobLock, games, roms, mirror, reader, systems, paths, audit, auditPaths, time, logger);

    internal Sg1000Mover Sg1000Mover => field ??= new(LibraryEditSupport, ReleaseEditor, mirror, reader, systems, paths, audit, time, logger);

    /// <inheritdoc cref="Sg1000Mover.MoveAsync"/>
    /// <remarks>Takes no job lock: only <c>Sg1000MoveJob</c>, which holds it, calls it. Not cancellable once started.</remarks>
    internal Task<LibraryEditResult> MoveToSg1000Async(GameRow row, AuditActor actor, CancellationToken ct) =>
        Sg1000Mover.MoveAsync(row, actor);

    /// <inheritdoc cref="ReleaseEditor.DeleteReleaseAsync"/>
    public Task<LibraryEditResult> DeleteReleaseAsync(
        Guid gameId,
        long romId,
        AuditActor actor,
        CancellationToken ct) =>
        ReleaseEditor.DeleteReleaseAsync(gameId, romId, actor, ct);

    /// <inheritdoc cref="ReleaseEditor.SetPrimaryReleaseAsync"/>
    public Task<LibraryEditResult> SetPrimaryReleaseAsync(
        Guid gameId,
        long romId,
        AuditActor actor,
        CancellationToken ct) =>
        ReleaseEditor.SetPrimaryReleaseAsync(gameId, romId, actor, ct);

    /// <inheritdoc cref="ReleaseEditor.EditReleaseAsync"/>
    public Task<LibraryEditResult> EditReleaseAsync(
        Guid gameId,
        long romId,
        ReleaseEdit patch,
        AuditActor actor,
        CancellationToken ct) =>
        ReleaseEditor.EditReleaseAsync(gameId, romId, patch, actor, ct);

    /// <inheritdoc cref="ReleaseEditor.MoveReleaseAsync"/>
    public Task<LibraryEditResult> MoveReleaseAsync(
        Guid gameId,
        long romId,
        Guid? targetGameId,
        string? newGameTitle,
        AuditActor actor,
        CancellationToken ct) =>
        ReleaseEditor.MoveReleaseAsync(gameId, romId, targetGameId, newGameTitle, actor, ct);

    /// <inheritdoc cref="ReleaseEditor.MergeAsync"/>
    public Task<LibraryEditResult> MergeAsync(
        Guid gameId,
        Guid targetGameId,
        AuditActor actor,
        CancellationToken ct) =>
        ReleaseEditor.MergeAsync(gameId, targetGameId, actor, ct);

    /// <inheritdoc cref="ImageEditor.AddImageAsync"/>
    public Task<LibraryEditResult> AddImageAsync(
        Guid gameId,
        ImageType type,
        byte[] bytes,
        AuditActor actor,
        CancellationToken ct,
        string? sourceUrl = null) =>
        ImageEditor.AddImageAsync(gameId, type, bytes, actor, ct, sourceUrl);

    /// <inheritdoc cref="ImageEditor.DeleteImageAsync"/>
    public Task<LibraryEditResult> DeleteImageAsync(
        Guid gameId,
        long imageId,
        AuditActor actor,
        CancellationToken ct) =>
        ImageEditor.DeleteImageAsync(gameId, imageId, actor, ct);

    /// <inheritdoc cref="ImageEditor.SetCoverAsync"/>
    public Task<LibraryEditResult> SetCoverAsync(
        Guid gameId,
        long imageId,
        AuditActor actor,
        CancellationToken ct) =>
        ImageEditor.SetCoverAsync(gameId, imageId, actor, ct);

    /// <inheritdoc cref="ImageEditor.RetryMediaAsync"/>
    public Task<LibraryEditResult> RetryMediaAsync(Guid gameId, AuditActor actor, CancellationToken ct) =>
        ImageEditor.RetryMediaAsync(gameId, actor, ct);

    /// <inheritdoc cref="SaveEditor.AddSaveAsync"/>
    public Task<LibraryEditResult> AddSaveAsync(
        Guid gameId,
        long romId,
        string kind,
        string name,
        string? description,
        SaveUpload file,
        AuditActor actor,
        CancellationToken ct) =>
        SaveEditor.AddSaveAsync(gameId, romId, kind, name, description, file, actor, ct);

    /// <inheritdoc cref="SaveEditor.UpdateSaveAsync"/>
    public Task<LibraryEditResult> UpdateSaveAsync(
        Guid gameId,
        string saveId,
        string? name,
        string? description,
        AuditActor actor,
        SaveViewer viewer,
        CancellationToken ct) =>
        SaveEditor.UpdateSaveAsync(gameId, saveId, name, description, actor, viewer, ct);

    /// <inheritdoc cref="SaveEditor.ReplaceSaveFileAsync"/>
    public Task<LibraryEditResult> ReplaceSaveFileAsync(
        Guid gameId,
        string saveId,
        SaveUpload file,
        AuditActor actor,
        SaveViewer viewer,
        CancellationToken ct) =>
        SaveEditor.ReplaceSaveFileAsync(gameId, saveId, file, actor, viewer, ct);

    /// <inheritdoc cref="SaveEditor.DeleteSaveAsync"/>
    public Task<LibraryEditResult> DeleteSaveAsync(
        Guid gameId,
        string saveId,
        AuditActor actor,
        SaveViewer viewer,
        CancellationToken ct) =>
        SaveEditor.DeleteSaveAsync(gameId, saveId, actor, viewer, ct);

    /// <inheritdoc cref="SaveEditor.PrecheckSaveAsync"/>
    public Task<LibraryEditResult?> PrecheckSaveAsync(
        Guid gameId,
        long? romId,
        string? saveId,
        SaveViewer viewer,
        CancellationToken ct) =>
        SaveEditor.PrecheckSaveAsync(gameId, romId, saveId, viewer, ct);

    /// <summary>Test seams of the external edit path: points where a test makes a move or the ZIP write fail.</summary>
    public ExternalEditHooks? ExternalHooks
    {
        get => LibraryEditSupport.ExternalHooks;
        set => LibraryEditSupport.ExternalHooks = value;
    }

    /// <param name="actor">The editing user; null (the rating/favourite endpoint) records nothing.</param>
    public async Task<LibraryEditResult> PatchAsync(
        Guid gameId,
        GamePatch patch,
        AuditActor? actor,
        CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        if (LibraryEditSupport.LegacyRefusal(row) is { } legacy)
            return legacy;
        if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            return await PatchExternalAsync(row, patch, actor).ConfigureAwait(false);
        var target = LibraryEditSupport.OpenForEdit(row);
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
                zipPath = LibraryEditSupport.NewZipPath(game.System, title);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(IoError, ex.Message);
            }
        }

        // shortcut: a rating/favourite change rewrites the whole ZIP (fine for small GB/GBC ROMs); add a cheaper
        // info.json-only update path if larger systems make rating slow.
        var result = await LibraryEditSupport.WriteAsync(existing, zipPath, game).ConfigureAwait(false);
        if (actor is not null && LibraryEditSupport.FilesChanged(result) && ChangedFields(current, game) is { Count: > 0 } changed)
            audit.Record(new AuditEvent(AuditActions.GameUpdated, actor, game.System, gameId, AuditPaths.Library(row.ZipPath),
                AuditPaths.Library(zipPath), string.Join(", ", changed)));
        if (result.Code is null && zipPath != row.ZipPath)
            LibraryEditSupport.DeleteOldZip(row.ZipPath, gameId);
        if (result.Code is null)
            logger.LogInformation("Game {GameId} edited ({ZipPath})", gameId, zipPath);
        await LibraryEditSupport.ClearIgdbSyncAsync(current, game, result).ConfigureAwait(false);
        return result;
    }

    private InfoJsonGame Patched(InfoJsonGame current, GamePatch patch)
    {
        var title = patch.Title ?? current.Title;
        return LibraryEditSupport.Relinked(current, current with
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
            IgdbId = patch.SetIgdbId ? patch.IgdbId : current.IgdbId,
            Metadata = LibraryEditSupport.Touched(current),
        });
    }

    /// <summary>
    /// B5/B7: writes the release's note (already normalised, ≤ <see cref="LibraryZip.MaxNoteBytes"/>), or deletes it when
    /// <paramref name="text"/> is empty (no error when there was none). Any signed-in user; not audited; the game's fields,
    /// including <c>updatedAt</c>, stay as they are.
    /// </summary>
    public Task<LibraryEditResult> SaveNoteAsync(
        Guid gameId,
        long romId,
        string text,
        int? userId,
        CancellationToken ct) =>
        WriteNoteAsync(gameId, romId, text.Length == 0 ? null : System.Text.Encoding.UTF8.GetBytes(text), missingIsNotFound: false, userId, ct);

    /// <summary>B6: deletes the release's note; <c>NotFound</c> when there is none.</summary>
    public Task<LibraryEditResult> DeleteNoteAsync(
        Guid gameId,
        long romId,
        int? userId,
        CancellationToken ct) =>
        WriteNoteAsync(gameId, romId, null, missingIsNotFound: true, userId, ct);

    private async Task<LibraryEditResult> WriteNoteAsync(
        Guid gameId,
        long romId,
        byte[]? bytes,
        bool missingIsNotFound,
        int? userId,
        CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        if (await roms.GetAsync(gameId, romId, ct).ConfigureAwait(false) is not { } romRow
            || await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        if (LibraryEditSupport.LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = LibraryEditSupport.OpenForEdit(row);
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
        var result = await LibraryEditSupport.WriteAsync(existing, row.ZipPath, current, entries, removed).ConfigureAwait(false);
        if (result.Code is null)
            logger.LogInformation("User {UserId} {Action} note {Entry} of game {GameId}", userId, bytes is null ? "deleted" : "saved", entry, gameId);
        return result;
    }

    public async Task<LibraryEditResult> DeleteGameAsync(Guid gameId, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        if (LibraryEditSupport.LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = LibraryEditSupport.OpenForEdit(row);
        if (target.Code is not null)
            return new(target.Code, target.Details);

        using var existing = target.Zip!;
        var result = await GameRemover.RemoveGameAsync(existing, row, target.Game!, actor).ConfigureAwait(false);
        if (result.Code is null)
            logger.LogInformation("Game {GameId} deleted ({ZipPath})", gameId, row.ZipPath);
        return result;
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
        if (before.IgdbId != after.IgdbId) changed.Add("igdbId");
        return changed;
    }

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
            var moved = ExternalRomMove.Move(library, move.From, move.To, move.Snapshot, logger, hooks: LibraryEditSupport.ExternalHooks?.Move);
            if (moved.Status != ExternalMoveStatus.Moved)
            {
                LibraryEditSupport.UndoMoves(placed);
                return new(IoError, moved.Details);
            }
            placed.Add((move.From, moved.Path!, moved.Snapshot!));
        }

        ExternalFileSnapshot published;
        try
        {
            var touched = placed.ToDictionary(m => Path.GetFileName(m.To), m => m.Snapshot, StringComparer.OrdinalIgnoreCase);
            published = await LibraryEditSupport.Writer(game.System).WriteCheckedAsync(library, SafePath.Resolve(library, zipPath), existing,
                new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>(), ExternalFiles: touched), ct, LibraryEditSupport.ExternalHooks?.Writer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing {ZipPath} failed", zipPath);
            LibraryEditSupport.UndoMoves(placed);
            return LibraryEditSupport.WriteFailure(ex);
        }

        if (actor is not null && ChangedFields(current, game) is { Count: > 0 } changed)
            audit.Record(new AuditEvent(AuditActions.GameUpdated, actor, game.System, row.Id, AuditPaths.Library(row.ZipPath),
                AuditPaths.Library(zipPath), string.Join(", ", changed)));
        var result = await LibraryEditSupport.MirrorAsync(() =>
        {
            // W1: stamped from the published ZIP, and only while the path still names it.
            if (ExternalRomPath.ClassifyFile(library, SafePath.Resolve(library, zipPath)).Snapshot != published)
                throw new IOException($"{zipPath} is no longer the ZIP this edit published");
            return mirror.ReplaceAsync(new MirrorEntry(game, zipPath, published.Length, published.LastWriteTimeUtc), ct);
        }, zipPath).ConfigureAwait(false);
        if (result.Code is null && zipPath != row.ZipPath)
            LibraryEditSupport.DeleteOldZipIfUnchanged(row.ZipPath, existing.Snapshot!, row.Id);
        if (result.Code is null)
            logger.LogInformation("Game {GameId} edited ({ZipPath})", row.Id, zipPath);
        await LibraryEditSupport.ClearIgdbSyncAsync(current, game, result).ConfigureAwait(false);
        return result;
    }
}
