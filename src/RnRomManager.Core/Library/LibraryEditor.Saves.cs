using Microsoft.Extensions.Logging;
using RnRomManager.Core.Audit;
using static RnRomManager.Core.Library.LibraryEditResult;

namespace RnRomManager.Core.Library;

/// <summary>B8–B11: a save's bytes, already streamed to a temp file by the endpoint (size and SHA-1 counted on the way).</summary>
/// <param name="Extension">B1, from the upload's file name.</param>
public sealed record SaveUpload(string Path, string Extension, long SizeBytes, string Sha1);

/// <summary>B8–B13: the save operations. Each takes the job lock, starts from the ZIP's <c>saves.json</c> and rewrites the ZIP.</summary>
public sealed partial class LibraryEditor
{
    /// <summary>B8: adds a save for the release <paramref name="romId"/>.</summary>
    public async Task<LibraryEditResult> AddSaveAsync(Guid gameId, long romId, string kind, string name, string? description, SaveUpload file,
        AuditActor actor, CancellationToken ct)
    {
        if (!GameSaves.IsKind(kind))
            return Invalid("kind", "Choose State or Battery.");
        if (GameSaves.NameProblem(name, out var trimmed) is { } nameProblem)
            return Invalid("name", nameProblem);
        if (GameSaves.DescriptionProblem(description) is { } descriptionProblem)
            return Invalid("description", descriptionProblem);

        using var held = jobLock.TryAcquire();
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
        if (saves!.Count(s => GameSaves.Names(s, rom.FileName)) >= GameSaves.MaxPerRelease)
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
    public async Task<LibraryEditResult> UpdateSaveAsync(Guid gameId, string saveId, string? name, string? description, AuditActor actor,
        CancellationToken ct)
    {
        var trimmed = (string?)null;
        if (name is not null && GameSaves.NameProblem(name, out trimmed) is { } nameProblem)
            return Invalid("name", nameProblem);
        if (GameSaves.DescriptionProblem(description) is { } descriptionProblem)
            return Invalid("description", descriptionProblem);

        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        var (error, zip, row, game, saves, current) = await OpenSaveAsync(gameId, saveId, ct).ConfigureAwait(false);
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
    public async Task<LibraryEditResult> ReplaceSaveFileAsync(Guid gameId, string saveId, SaveUpload file, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        var (error, zip, row, game, saves, current) = await OpenSaveAsync(gameId, saveId, ct).ConfigureAwait(false);
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
    public async Task<LibraryEditResult> DeleteSaveAsync(Guid gameId, string saveId, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        var (error, zip, row, game, saves, current) = await OpenSaveAsync(gameId, saveId, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        using var _ = zip;
        return await WriteSavesAsync(zip!, row!, game!, [.. saves!.Where(s => s.Id != saveId)], current!, AuditActions.SaveDeleted, actor, [],
            [current!.Entry]).ConfigureAwait(false);
    }

    /// <summary>
    /// The game's ZIP and saves, in the B8 order after the game lookup: <c>StorageMigrationPending</c>, then
    /// <c>SavesUnreadable</c>. The caller disposes the ZIP.
    /// </summary>
    private (LibraryEditResult? Error, LibraryZip? Zip, InfoJsonGame? Game, IReadOnlyList<GameSave>? Saves) OpenSaves(GameRow row)
    {
        if (LegacyRefusal(row) is { } legacy)
            return (legacy, null, null, null);
        var target = OpenForEdit(row);
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

    /// <summary><see cref="OpenSaves"/> for one save; an unknown id is <c>NotFound</c> (B9: after <c>SavesUnreadable</c>).</summary>
    private async Task<(LibraryEditResult? Error, LibraryZip? Zip, GameRow? Row, InfoJsonGame? Game, IReadOnlyList<GameSave>? Saves, GameSave? Save)>
        OpenSaveAsync(Guid gameId, string saveId, CancellationToken ct)
    {
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return (new(NotFound), null, null, null, null, null);
        var (error, zip, game, saves) = OpenSaves(row);
        if (error is not null)
            return (error, null, null, null, null, null);
        if (saves!.FirstOrDefault(s => s.Id == saveId) is not { } save)
        {
            zip!.Dispose();
            return (new(NotFound), null, null, null, null, null);
        }
        return (null, zip, row, game, saves, save);
    }

    /// <summary>
    /// B5: the target write's saves when a merge or a last-release move carries the source's saves: the target's records,
    /// then the source's (a clashing id gets a new one), each source record's <c>release</c> renamed as its release was
    /// (<paramref name="renames"/>, matched ignoring case; an orphan stays as it is), their bytes streamed from the source.
    /// </summary>
    /// <param name="target">The target's ZIP; null for a new game.</param>
    /// <returns><c>SavesUnreadable</c> (either side) or <c>SavesIndexFull</c>; else the change, null when the source has no saves.</returns>
    private static (LibraryEditResult? Error, SavesChange? Change) CarrySaves(LibraryZip source, LibraryZip? target,
        IReadOnlyDictionary<string, string> renames)
    {
        var from = GameSaves.Read(source);
        var into = target is null ? new SavesReadResult(SavesState.Absent) : GameSaves.Read(target);
        if (from.State == SavesState.Unreadable || into.State == SavesState.Unreadable)
            return (new(SavesUnreadable, SavesUnreadableTitle), null);
        if (from.State == SavesState.Absent)
            return (null, null);

        var records = new List<GameSave>(into.Index?.Saves ?? []);
        var ids = records.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var added = new Dictionary<string, Func<Stream>>();
        foreach (var save in from.Index!.Saves)
        {
            var carried = ids.Add(save.Id) ? save : save with { Id = GameSaves.NewId() };
            ids.Add(carried.Id);
            if (renames.TryGetValue(save.Release, out var renamed))
                carried = carried with { Release = renamed };
            added[carried.Entry] = () => source.OpenEntry(save.Entry, save.SizeBytes);
            records.Add(carried);
        }
        return GameSaves.Serialize(records) is { } index
            ? (null, new SavesChange(index, added, new HashSet<string>()))
            : (new(SavesIndexFull, SavesIndexFullTitle), null);
    }

    /// <summary>Writes the new <c>saves.json</c> (none when <paramref name="saves"/> is empty), mirrors the ZIP and audits (B13).</summary>
    private async Task<LibraryEditResult> WriteSavesAsync(LibraryZip zip, GameRow row, InfoJsonGame game, IReadOnlyList<GameSave> saves, GameSave save,
        string action, AuditActor actor, Dictionary<string, Func<Stream>> added, HashSet<string> removed)
    {
        byte[]? index = null;
        if (saves.Count > 0 && (index = GameSaves.Serialize(saves)) is null)
            return new(SavesIndexFull, SavesIndexFullTitle);
        var result = await WriteAsync(zip, row.ZipPath, game, saves: new SavesChange(index, added, removed)).ConfigureAwait(false);
        if (FilesChanged(result))
            audit.Record(new AuditEvent(action, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                Details: $"{save.Kind} \"{save.Name}\" ({save.Id})"));
        if (result.Code is null)
            logger.LogInformation("{Action} {SaveId} in game {GameId} ({ZipPath})", action, save.Id, row.Id, row.ZipPath);
        return result with { Save = save };
    }
}
