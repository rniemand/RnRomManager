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

/// <summary>B8–B13: the save operations. Each takes the job lock, starts from the ZIP's <c>saves.json</c> and rewrites the ZIP.</summary>
internal sealed class SaveEditor(
    LibraryEditSupport libraryEditSupport,
    JobLock jobLock,
    IGameRepository games,
    IRomRepository roms,
    IAuditLog audit,
    TimeProvider time,
    ILogger<LibraryEditor> logger)
{
    /// <summary>B8: adds a save for the release <paramref name="romId"/>.</summary>
    internal async Task<LibraryEditResult> AddSaveAsync(
        Guid gameId,
        long romId,
        string kind,
        string name,
        string? description,
        SaveUpload file,
        AuditActor actor,
        CancellationToken ct)
    {
        if (!GameSaves.IsKind(kind))
            return Invalid("kind", "Choose State or Battery.");
        if (GameSaves.NameProblem(name, out var trimmed) is { } nameProblem)
            return Invalid("name", nameProblem);
        if (GameSaves.DescriptionProblem(description) is { } descriptionProblem)
            return Invalid("description", descriptionProblem);

        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row
            || await roms.GetAsync(gameId, romId, ct).ConfigureAwait(false) is not { } romRow)
            return new(NotFound);
        var (error, zip, game, saves) = OpenSaves(row);
        if (error is not null)
            return error;
        using var _ = zip;
        if (game!.Roms.FirstOrDefault(r => r.Metadata.Sha1 == romRow.Sha1) is not { } rom)
            return new(NotFound, $"{row.ZipPath} no longer lists SHA1 {romRow.Sha1}");
        if (OwnSavesOn(saves!, rom.FileName, actor.Username) >= GameSaves.MaxPerRelease)
            return new(SaveLimitReached, SaveLimitReachedTitle);

        var now = time.GetUtcNow();
        var save = new GameSave
        {
            Id = GameSaves.NewId(), Name = trimmed, Description = description ?? "", Kind = kind, Release = rom.FileName,
            Extension = file.Extension, SizeBytes = file.SizeBytes, Sha1 = file.Sha1, UploadedBy = actor.Username, UploadedAt = now,
            UpdatedBy = actor.Username, UpdatedAt = now,
        };
        return await WriteSavesAsync(zip!, row, game, [.. saves!, save], save, AuditActions.SaveAdded, actor,
            new Dictionary<string, Func<Stream>> { [save.Entry] = () => File.OpenRead(file.Path) }, []).ConfigureAwait(false);
    }

    /// <summary>B9: changes a save's name and/or description; a change to neither writes nothing.</summary>
    internal async Task<LibraryEditResult> UpdateSaveAsync(
        Guid gameId,
        string saveId,
        string? name,
        string? description,
        AuditActor actor,
        SaveViewer viewer,
        CancellationToken ct)
    {
        var trimmed = (string?)null;
        if (name is not null && GameSaves.NameProblem(name, out trimmed) is { } nameProblem)
            return Invalid("name", nameProblem);
        if (GameSaves.DescriptionProblem(description) is { } descriptionProblem)
            return Invalid("description", descriptionProblem);

        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        var (error, zip, row, game, saves, current) = await OpenSaveAsync(gameId, saveId, viewer, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        using var _ = zip;
        var updated = current! with { Name = trimmed ?? current.Name, Description = description ?? current.Description };
        if (updated == current)
            return Ok with { Save = current };
        updated = updated with { UpdatedBy = actor.Username, UpdatedAt = time.GetUtcNow() };
        return await WriteSavesAsync(zip!, row!, game!, [.. saves!.Select(s => s.Id == saveId ? updated : s)], updated, AuditActions.SaveUpdated,
            actor, [], []).ConfigureAwait(false);
    }

    /// <summary>B10: replaces a save's bytes; the entry is renamed when the extension changes.</summary>
    internal async Task<LibraryEditResult> ReplaceSaveFileAsync(
        Guid gameId,
        string saveId,
        SaveUpload file,
        AuditActor actor,
        SaveViewer viewer,
        CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        var (error, zip, row, game, saves, current) = await OpenSaveAsync(gameId, saveId, viewer, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        using var _ = zip;
        var updated = current! with
        {
            Extension = file.Extension, SizeBytes = file.SizeBytes, Sha1 = file.Sha1, UpdatedBy = actor.Username, UpdatedAt = time.GetUtcNow(),
        };
        return await WriteSavesAsync(zip!, row!, game!, [.. saves!.Select(s => s.Id == saveId ? updated : s)], updated, AuditActions.SaveUpdated,
            actor, new Dictionary<string, Func<Stream>> { [updated.Entry] = () => File.OpenRead(file.Path) }, [current.Entry]).ConfigureAwait(false);
    }

    /// <summary>B11: removes a save, and <c>saves.json</c> with the last one.</summary>
    internal async Task<LibraryEditResult> DeleteSaveAsync(
        Guid gameId,
        string saveId,
        AuditActor actor,
        SaveViewer viewer,
        CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        var (error, zip, row, game, saves, current) = await OpenSaveAsync(gameId, saveId, viewer, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        using var _ = zip;
        return await WriteSavesAsync(zip!, row!, game!, [.. saves!.Where(s => s.Id != saveId)], current!, AuditActions.SaveDeleted, actor, [],
            [current!.Entry]).ConfigureAwait(false);
    }

    /// <summary>
    /// B8–B11: the cheap checks an endpoint runs before it reads a file, in the documented order: <c>404</c> game, then
    /// release (<paramref name="romId"/>), <c>StorageMigrationPending</c>, <c>JobRunning</c> (a probe of the lock,
    /// released at once), <c>SavesUnreadable</c>, then <c>404</c> save (<paramref name="saveId"/>) or, for an add,
    /// <c>SaveLimitReached</c> (the viewer's own saves on that release, S4). A save the viewer can't see answers like an
    /// unknown id (S3). The operation checks them all again under the lock.
    /// </summary>
    /// <returns>null when the operation may go ahead.</returns>
    internal async Task<LibraryEditResult?> PrecheckSaveAsync(
        Guid gameId,
        long? romId,
        string? saveId,
        SaveViewer viewer,
        CancellationToken ct)
    {
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        RomRow? romRow = null;
        if (romId is { } id && (romRow = await roms.GetAsync(gameId, id, ct).ConfigureAwait(false)) is null)
            return new(NotFound);
        if (libraryEditSupport.LegacyRefusal(row) is { } legacy)
            return legacy;
        using (var probe = jobLock.TryAcquire("Saving changes"))
        {
            if (probe is null)
                return new(JobRunning);
        }
        var (error, zip, game, saves) = OpenSaves(row);
        if (error is not null)
            return error;
        using var _ = zip;
        if (saveId is not null && !saves!.Any(s => s.Id == saveId && viewer.CanSee(s)))
            return new(NotFound);
        if (romRow is not null && game!.Roms.FirstOrDefault(r => r.Metadata.Sha1 == romRow.Sha1) is { } rom
            && OwnSavesOn(saves!, rom.FileName, viewer.Username) >= GameSaves.MaxPerRelease)
            return new(SaveLimitReached, SaveLimitReachedTitle);
        return null;
    }

    /// <summary>
    /// The game's ZIP and saves, in the B8 order after the game lookup: <c>StorageMigrationPending</c>, then
    /// <c>SavesUnreadable</c>. The caller disposes the ZIP.
    /// </summary>
    internal (LibraryEditResult? Error, LibraryZip? Zip, InfoJsonGame? Game, IReadOnlyList<GameSave>? Saves) OpenSaves(GameRow row)
    {
        if (libraryEditSupport.LegacyRefusal(row) is { } legacy)
            return (legacy, null, null, null);
        var target = libraryEditSupport.OpenForEdit(row);
        if (target.Code is not null)
            return (new(target.Code, target.Details), null, null, null);
        var read = GameSaves.Read(target.Zip!);
        if (read.State == SavesState.Unreadable)
        {
            target.Zip!.Dispose();
            return (new(SavesUnreadable, SavesUnreadableTitle), null, null, null);
        }
        return (null, target.Zip, target.Game, read.Index?.Saves ?? []);
    }

    /// <summary>S4: <c>MaxPerRelease</c> counts only <paramref name="username"/>'s own saves on the release.</summary>
    internal static int OwnSavesOn(IReadOnlyList<GameSave> saves, string releaseFileName, string? username) =>
        saves.Count(s => GameSaves.Names(s, releaseFileName) && GameSaves.IsOwner(s, username));

    /// <summary>
    /// <see cref="OpenSaves"/> for one save; an unknown id, or one the viewer can't see (S3), is <c>NotFound</c> (B9: after <c>SavesUnreadable</c>).
    /// </summary>
    internal async Task<(LibraryEditResult? Error, LibraryZip? Zip, GameRow? Row, InfoJsonGame? Game, IReadOnlyList<GameSave>? Saves, GameSave? Save)>
        OpenSaveAsync(Guid gameId, string saveId, SaveViewer viewer, CancellationToken ct)
    {
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return (new(NotFound), null, null, null, null, null);
        var (error, zip, game, saves) = OpenSaves(row);
        if (error is not null)
            return (error, null, null, null, null, null);
        if (saves!.FirstOrDefault(s => s.Id == saveId && viewer.CanSee(s)) is not { } save)
        {
            zip!.Dispose();
            return (new(NotFound), null, null, null, null, null);
        }
        return (null, zip, row, game, saves, save);
    }

    /// <summary>Writes the new <c>saves.json</c> (none when <paramref name="saves"/> is empty), mirrors the ZIP and audits (B13).</summary>
    internal async Task<LibraryEditResult> WriteSavesAsync(
        LibraryZip zip,
        GameRow row,
        InfoJsonGame game,
        IReadOnlyList<GameSave> saves,
        GameSave save,
        string action,
        AuditActor actor,
        Dictionary<string, Func<Stream>> added,
        HashSet<string> removed)
    {
        // B18: every save write leaves a version 2 index.
        saves = GameSaves.Upgrade(saves, s => () => zip.OpenEntry(s.Entry, s.SizeBytes), added, removed);
        if (save.Legacy)
            save = saves.FirstOrDefault(s => s.Id == save.Id) ?? save with { Legacy = false };
        byte[]? index = null;
        if (saves.Count > 0 && (index = GameSaves.Serialize(saves)) is null)
            return new(SavesIndexFull, SavesIndexFullTitle);
        var result = await libraryEditSupport.WriteAsync(zip, row.ZipPath, game, saves: new SavesChange(index, added, removed)).ConfigureAwait(false);
        if (LibraryEditSupport.FilesChanged(result))
            audit.Record(new AuditEvent(action, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                Details: $"{save.Kind} \"{save.Name}\" ({save.Id})"));
        if (result.Code is null)
            logger.LogInformation("{Action} {SaveId} in game {GameId} ({ZipPath})", action, save.Id, row.Id, row.ZipPath);
        return result with { Save = save };
    }
}
