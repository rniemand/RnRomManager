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
/// External-storage paths (W2–W5) are in <c>LibraryEditor.External.cs</c>.
/// </summary>
public sealed partial class LibraryEditor(
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

    public async Task<LibraryEditResult> DeleteReleaseAsync(
        Guid gameId,
        long romId,
        AuditActor actor,
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
        // The DB row only finds the release; the ZIP's info.json decides what's there (§6.1).
        if (current.Roms.FirstOrDefault(r => r.Metadata.Sha1 == romRow.Sha1) is not { } rom)
            return new(NotFound, $"{row.ZipPath} no longer lists SHA1 {romRow.Sha1}");

        var remaining = current.Roms.Where(r => !ReferenceEquals(r, rom)).ToList();
        if (remaining.Count == 0)
            return await RemoveGameAsync(existing, row, current, actor).ConfigureAwait(false); // the last release removes the game
        var rebuilt = current with { Roms = PrimaryRelease.Apply(remaining, LibraryEditSupport.DatSourceOf(row.SystemId), current.ChosenPrimarySha1), Metadata = LibraryEditSupport.Touched(current) };
        if (LibraryEditSupport.IsExternal(row))
        {
            var external = await RemoveExternalAsync(existing, row, [rom], rebuilt, actor).ConfigureAwait(false);
            if (external.Code is null)
                logger.LogInformation("Release {FileName} removed from game {GameId} ({ZipPath})", rom.FileName, gameId, row.ZipPath);
            return external;
        }
        var (result, copies) = await RemoveRomsAsync(existing, current, [rom], () => LibraryEditSupport.WriteAsync(existing, row.ZipPath, rebuilt)).ConfigureAwait(false);
        if (LibraryEditSupport.FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.ReleaseDeleted, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
                copies.Count > 0 ? auditPaths.Of(copies[0]) : null, rom.FileName));
        if (result.Code is null)
            logger.LogInformation("Release {FileName} removed from game {GameId} ({ZipPath})", rom.FileName, gameId, row.ZipPath);
        return result;
    }

    /// <summary>
    /// Makes the release the game's primary (an Admin override of the ranker) and records it as
    /// <see cref="InfoJsonGame.ChosenPrimarySha1"/>, so it stays primary as releases come and go; a no-op when it already is.
    /// </summary>
    public async Task<LibraryEditResult> SetPrimaryReleaseAsync(
        Guid gameId,
        long romId,
        AuditActor actor,
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
        if (rom.IsPrimary && current.ChosenPrimarySha1 == rom.Metadata.Sha1)
            return Ok;

        var game = current with
        {
            ChosenPrimarySha1 = rom.Metadata.Sha1,
            Roms = current.Roms.Select(r => r with { IsPrimary = ReferenceEquals(r, rom) }).ToList(),
            Metadata = LibraryEditSupport.Touched(current),
        };
        var result = await LibraryEditSupport.WriteAsync(existing, row.ZipPath, game).ConfigureAwait(false);
        if (LibraryEditSupport.FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.PrimarySet, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath), Details: rom.FileName));
        if (result.Code is null)
            logger.LogInformation("Release {FileName} is now the primary of game {GameId}", rom.FileName, gameId);
        return result;
    }

    /// <summary>
    /// B5–B10: changes a release's tags and renames it to match (B7), except on a <c>FileSets</c> system. The checks that
    /// need the game (disc on a non-disc system, unknown regions, a version on a non-update) come back as <c>ValidationFailed</c>.
    /// </summary>
    public async Task<LibraryEditResult> EditReleaseAsync(
        Guid gameId,
        long romId,
        ReleaseEdit patch,
        AuditActor actor,
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
        var fileSets = systems.ById(row.SystemId) is { FileSets: true };

        if (patch.SetDisc && patch.Disc is not null && !fileSets)
            return Invalid("disc", "Disc numbers are for disc systems.");
        if (patch.Regions?.Any(r => !NoIntroRegions.All.Contains(r) && !rom.Regions.Contains(r, StringComparer.Ordinal)) == true)
            return Invalid("regions", "Must be known No-Intro regions.");
        var kind = patch.Kind ?? KindOf(rom.Flags);
        var version = patch.SetVersion ? patch.Version : rom.Version;
        if (patch.SetVersion && version is not null && kind != ReleaseEdit.KindUpdate)
            return Invalid("version", "Only an update has a version.");

        var edited = rom with
        {
            Regions = patch.Regions ?? rom.Regions,
            Languages = patch.Languages ?? rom.Languages,
            Revision = patch.SetRevision ? patch.Revision : rom.Revision,
            Version = kind == ReleaseEdit.KindUpdate ? version : null,
            Flags = EditedFlags(rom.Flags, patch, kind),
        };
        if (edited.Regions.SequenceEqual(rom.Regions, StringComparer.Ordinal) && edited.Languages.SequenceEqual(rom.Languages, StringComparer.Ordinal)
            && edited.Flags.SequenceEqual(rom.Flags, StringComparer.Ordinal) && edited.Revision == rom.Revision && edited.Version == rom.Version)
            return Ok with { FileName = rom.FileName }; // B6

        var others = current.Roms.Where(r => !ReferenceEquals(r, rom)).Select(r => r.FileName).ToList();
        var name = fileSets ? rom.FileName : ReleaseFileName.Edit(rom.FileName, rom.BaseTitle, Tags(rom), Tags(edited));
        if (string.Equals(name, rom.FileName, StringComparison.OrdinalIgnoreCase))
            name = rom.FileName;
        else
            name = LibraryEditSupport.IsExternal(row)
                ? ExternalRomName.AllocateLogicalName(name, new ExternalNameReservations(others))
                : LibraryEditSupport.FreeName(name, others);
        edited = edited with { FileName = name };

        var entries = new Dictionary<string, Func<Stream>>();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        SavesChange? saves = null;
        if (name != rom.FileName)
        {
            // B9: saves are rewritten only when one names the release; an unreadable index can't tell, so it refuses.
            var read = GameSaves.Read(existing);
            if (read.State == SavesState.Unreadable)
                return new(SavesUnreadable, SavesUnreadableTitle);
            if (read.Index?.Saves.Any(s => GameSaves.Names(s, rom.FileName)) == true)
            {
                var (savesError, change) = LibraryEditSupport.CarrySaves(existing, null, LibraryEditSupport.Renames((rom.FileName, name)));
                if (savesError is not null)
                    return savesError;
                saves = change;
            }
            if (!LibraryEditSupport.IsExternal(row))
            {
                entries[LibraryZip.RomEntry(name)] = () => existing.OpenEntry(LibraryZip.RomEntry(rom.FileName), rom.Metadata.SizeBytes);
                removed.Add(LibraryZip.RomEntry(rom.FileName));
            }
            LibraryEditSupport.CarryNote(existing, rom.FileName, name, entries, removed);
        }

        // An add-on is never kept as an Admin-chosen primary.
        var chosen = current.ChosenPrimarySha1 == rom.Metadata.Sha1 && kind != ReleaseEdit.KindRelease ? null : current.ChosenPrimarySha1;
        var game = current with
        {
            ChosenPrimarySha1 = chosen,
            Roms = PrimaryRelease.Apply([.. current.Roms.Select(r => ReferenceEquals(r, rom) ? edited : r)], LibraryEditSupport.DatSourceOf(row.SystemId), chosen),
            Metadata = LibraryEditSupport.Touched(current),
        };
        var result = await LibraryEditSupport.WriteAsync(existing, row.ZipPath, game, entries, removed, saves).ConfigureAwait(false);
        if (LibraryEditSupport.FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.ReleaseEdited, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
                Details: name == rom.FileName ? rom.FileName : $"{rom.FileName} → {name}"));
        if (result.Code is null)
            logger.LogInformation("Release {FileName} of game {GameId} edited", rom.FileName, gameId);
        return result with { FileName = name };
    }

    /// <summary>DLC B1: <c>Update</c> wins over <c>DLC</c>.</summary>
    private static string KindOf(IReadOnlyList<string> flags) =>
        HasFlag(flags, ReleaseVersion.UpdateFlag) ? ReleaseEdit.KindUpdate : HasFlag(flags, "DLC") ? ReleaseEdit.KindDlc : ReleaseEdit.KindRelease;

    private static bool HasFlag(IReadOnlyList<string> flags, string flag) => flags.Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static ReleaseTags Tags(InfoJsonRom rom) =>
        new(rom.Regions, rom.Languages, rom.Revision, HasFlag(rom.Flags, "Hack"), HasFlag(rom.Flags, "Translated"), rom.Version);

    /// <summary>B5: flags keep their order; a flag turned on is appended, a changed disc replaces its flag in place.</summary>
    private static List<string> EditedFlags(IReadOnlyList<string> flags, ReleaseEdit patch, string kind)
    {
        var result = flags.ToList();
        void Set(string flag, bool on)
        {
            if (!on)
                result.RemoveAll(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase));
            else if (!HasFlag(result, flag))
                result.Add(flag);
        }
        if (patch.Translated is { } translated)
            Set("Translated", translated);
        if (patch.Hack is { } hack)
            Set("Hack", hack);
        if (patch.Kind is not null)
        {
            Set(ReleaseVersion.UpdateFlag, kind == ReleaseEdit.KindUpdate);
            Set("DLC", kind == ReleaseEdit.KindDlc);
        }
        if (patch.SetDisc)
        {
            var at = result.FindIndex(f => System.Text.RegularExpressions.Regex.IsMatch(f, @"^Disc \d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            if (patch.Disc is not { } disc)
            {
                if (at >= 0)
                    result.RemoveAt(at);
            }
            else if (at >= 0)
                result[at] = $"Disc {disc}";
            else
                result.Add($"Disc {disc}");
        }
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
        var result = await RemoveGameAsync(existing, row, target.Game!, actor).ConfigureAwait(false);
        if (result.Code is null)
            logger.LogInformation("Game {GameId} deleted ({ZipPath})", gameId, row.ZipPath);
        return result;
    }

    /// <summary>
    /// §11 move release, to an existing game (<paramref name="targetGameId"/>) or a new one (<paramref name="newGameTitle"/>,
    /// which runs §7.2 steps 7–8 for it). The release keeps its <c>baseTitle</c> (D23). Order: target ZIP (now holding the
    /// ROM), source ZIP rebuilt, one DB transaction, then an emptied source ZIP is deleted.
    /// </summary>
    public async Task<LibraryEditResult> MoveReleaseAsync(
        Guid gameId,
        long romId,
        Guid? targetGameId,
        string? newGameTitle,
        AuditActor actor,
        CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Saving changes");
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
        if ((LibraryEditSupport.LegacyRefusal(row) ?? (targetRow is null ? null : LibraryEditSupport.LegacyRefusal(targetRow))) is { } legacy)
            return legacy;
        if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            return await MoveReleaseExternalAsync(row, romRow, targetRow, newGameTitle, actor).ConfigureAwait(false);

        var source = LibraryEditSupport.OpenForEdit(row);
        if (source.Code is not null)
            return new(source.Code, source.Details);
        using var sourceZip = source.Zip!;
        if (source.Game!.Roms.FirstOrDefault(r => r.Metadata.Sha1 == romRow.Sha1) is not { } rom)
            return new(NotFound, $"{row.ZipPath} no longer lists SHA1 {romRow.Sha1}");
        var target = targetRow is null ? null : LibraryEditSupport.OpenForEdit(targetRow);
        if (target?.Code is not null)
            return new(target.Code, target.Details);
        using var targetZip = target?.Zip;

        var now = time.GetUtcNow();
        var moved = rom with { FileName = LibraryEditSupport.FreeName(rom.FileName, target?.Game!.Roms.Select(r => r.FileName) ?? []) };
        var entries = new Dictionary<string, Func<Stream>>
        {
            [LibraryZip.RomEntry(moved.FileName)] = () => sourceZip.OpenEntry(LibraryZip.RomEntry(rom.FileName), rom.Metadata.SizeBytes),
        };
        var removed = new HashSet<string>(StringComparer.Ordinal);
        LibraryEditSupport.CarryNote(sourceZip, rom.FileName, moved.FileName, entries, removed);

        InfoJsonGame targetGame;
        string targetPath;
        if (target is null)
        {
            targetGame = await LibraryEditSupport.NewGameForAsync(row.SystemId, rom, newGameTitle!, PrimaryRelease.Apply([moved], LibraryEditSupport.DatSourceOf(row.SystemId)), entries, now, ct)
                .ConfigureAwait(false);
            try
            {
                targetPath = LibraryEditSupport.NewZipPath(row.SystemId, newGameTitle!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(IoError, ex.Message);
            }
        }
        else
        {
            targetGame = target.Game! with { Roms = PrimaryRelease.Apply([.. target.Game.Roms, moved], LibraryEditSupport.DatSourceOf(row.SystemId), target.Game.ChosenPrimarySha1), Metadata = LibraryEditSupport.Touched(target.Game) };
            targetPath = targetRow!.ZipPath;
        }

        var remaining = source.Game.Roms.Where(r => !ReferenceEquals(r, rom)).ToList();
        var sourceGame = remaining.Count == 0 ? null : source.Game with { Roms = PrimaryRelease.Apply(remaining, LibraryEditSupport.DatSourceOf(row.SystemId), source.Game.ChosenPrimarySha1), Metadata = LibraryEditSupport.Touched(source.Game) };
        // B5: the last release deletes the source game, so its saves go with it into the target.
        var (savesError, saves) = sourceGame is null ? LibraryEditSupport.CarrySaves(sourceZip, targetZip, LibraryEditSupport.Renames((rom.FileName, moved.FileName))) : (null, null);
        if (savesError is not null)
            return savesError;

        if (await LibraryEditSupport.WriteZipAsync(targetZip, targetPath, targetGame, entries, removed, saves).ConfigureAwait(false) is { } failed)
            return failed;
        if (sourceGame is not null && await LibraryEditSupport.WriteZipAsync(sourceZip, row.ZipPath, sourceGame, []).ConfigureAwait(false) is { } sourceFailed)
        {
            await UndoTargetWriteAsync(targetPath, target?.Game).ConfigureAwait(false);
            return sourceFailed;
        }
        audit.Record(new AuditEvent(AuditActions.ReleaseMoved, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
            AuditPaths.Library(targetPath), rom.FileName));

        var result = await LibraryEditSupport.MirrorAsync(() => mirror.ApplyAsync(
            [LibraryEditSupport.Mirror(targetPath, targetGame), .. sourceGame is null ? Array.Empty<MirrorEntry>() : [LibraryEditSupport.Mirror(row.ZipPath, sourceGame)]],
            sourceGame is null ? [gameId] : [], CancellationToken.None), targetPath).ConfigureAwait(false);
        if (sourceGame is null)
        {
            sourceZip.Dispose(); // Windows can't delete an open file.
            LibraryEditSupport.DeleteOldZip(row.ZipPath, gameId);
        }
        if (result.Code is null)
        {
            logger.LogInformation("Release {FileName} moved from game {GameId} to {TargetId} ({ZipPath})", rom.FileName, gameId, targetGame.GameId, targetPath);
            if (sourceGame is null)
                await LibraryEditSupport.MoveCollectionMembershipsAsync(gameId, targetGame.GameId).ConfigureAwait(false);
        }
        return result with { GameId = targetGame.GameId };
    }

    /// <summary>
    /// §11 Merge into… (D35, I4a): every release of the source moves into the target in one write. The target keeps its
    /// title, metadata and cover; empty fields are filled from the source. The source's non-default images are copied in
    /// under new names (I2); its placeholder is dropped; a real source box art removes the target's placeholder (and becomes
    /// the cover if the placeholder was). More than <see cref="GameImages.MaxImages"/> images → <c>TooManyImages</c>.
    /// Target ZIP + DB commit, then the source ZIP is deleted.
    /// </summary>
    public async Task<LibraryEditResult> MergeAsync(
        Guid gameId,
        Guid targetGameId,
        AuditActor actor,
        CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        if (targetGameId == gameId)
            return Invalid("targetGameId", "Choose a different game.");
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row
            || await games.GetAsync(targetGameId, ct).ConfigureAwait(false) is not { } targetRow)
            return new(NotFound);
        if (targetRow.SystemId != row.SystemId)
            return Invalid("targetGameId", "The target game must be in the same system.");
        if ((LibraryEditSupport.LegacyRefusal(row) ?? LibraryEditSupport.LegacyRefusal(targetRow)) is { } legacy)
            return legacy;
        if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            return await MergeExternalAsync(row, targetRow, actor).ConfigureAwait(false);

        var source = LibraryEditSupport.OpenForEdit(row);
        if (source.Code is not null)
            return new(source.Code, source.Details);
        using var sourceZip = source.Zip!;
        var target = LibraryEditSupport.OpenForEdit(targetRow);
        if (target.Code is not null)
            return new(target.Code, target.Details);
        using var targetZip = target.Zip!;
        var (from, into) = (source.Game!, target.Game!);

        var entries = new Dictionary<string, Func<Stream>>();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var names = into.Roms.Select(r => r.FileName).ToList();
        var merged = new List<InfoJsonRom>(into.Roms);
        var renames = LibraryEditSupport.Renames();
        foreach (var rom in from.Roms)
        {
            var moved = rom with { FileName = LibraryEditSupport.FreeName(rom.FileName, names) };
            renames[rom.FileName] = moved.FileName;
            names.Add(moved.FileName);
            merged.Add(moved);
            entries[LibraryZip.RomEntry(moved.FileName)] = () => sourceZip.OpenEntry(LibraryZip.RomEntry(rom.FileName), rom.Metadata.SizeBytes);
            LibraryEditSupport.CarryNote(sourceZip, rom.FileName, moved.FileName, entries, removed);
        }

        if (GameImages.Merge(into.Images, from.Images) is not var (images, copies))
            return new(TooManyImages, $"The merged game would have more than {GameImages.MaxImages} images.");
        foreach (var (copy, sourceName) in copies)
            entries[LibraryZip.ImageEntry(copy.FileName)] = () => sourceZip.OpenEntry(LibraryZip.ImageEntry(sourceName), copy.SizeBytes);

        var targetGame = LibraryEditSupport.MergedInto(into, from, merged, images);
        var (savesError, saves) = LibraryEditSupport.CarrySaves(sourceZip, targetZip, renames);
        if (savesError is not null)
            return savesError;

        if (await LibraryEditSupport.WriteZipAsync(targetZip, targetRow.ZipPath, targetGame, entries, removed, saves).ConfigureAwait(false) is { } failed)
            return failed;
        audit.Record(new AuditEvent(AuditActions.GamesMerged, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
            AuditPaths.Library(targetRow.ZipPath), $"{from.Roms.Count} releases, {copies.Count} images"));
        var result = await LibraryEditSupport.MirrorAsync(() => mirror.ApplyAsync([LibraryEditSupport.Mirror(targetRow.ZipPath, targetGame)], [gameId], CancellationToken.None),
            targetRow.ZipPath).ConfigureAwait(false);
        sourceZip.Dispose();
        LibraryEditSupport.DeleteOldZip(row.ZipPath, gameId);
        if (result.Code is null)
        {
            logger.LogInformation("Game {GameId} merged into {TargetId} ({ZipPath})", gameId, targetGameId, targetRow.ZipPath);
            await LibraryEditSupport.MoveCollectionMembershipsAsync(gameId, targetGameId).ConfigureAwait(false);
        }
        await LibraryEditSupport.ClearIgdbSyncAsync(into, targetGame, result).ConfigureAwait(false);
        return result with { GameId = targetGameId };
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
            await LibraryEditSupport.Writer(before.System).WriteAsync(path, LibraryZip.Open(path, reader),
                new LibraryZipChanges(before, new Dictionary<string, Func<Stream>>())).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't undo the write of {ZipPath}; the moved ROM is in two ZIPs until one is fixed by hand", targetPath);
        }
    }

    /// <summary>Deletes the game's ZIP (its ROMs copied to <c>_removed/</c> first) and records <c>GameDeleted</c>.</summary>
    private async Task<LibraryEditResult> RemoveGameAsync(
        LibraryZip existing,
        GameRow row,
        InfoJsonGame current,
        AuditActor actor)
    {
        if (LibraryEditSupport.IsExternal(row))
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
            return await LibraryEditSupport.MirrorAsync(() => mirror.DeleteAsync(row.Id, CancellationToken.None), row.ZipPath).ConfigureAwait(false);
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
    private LibraryEditResult? CopyToRemoved(
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

    private const int CopyBufferBytes = 1024 * 1024;

    /// <summary>A failure reading a ZIP entry: over its recorded size, corrupt or CRC mismatch, or an IO error from the entry stream.</summary>
    private static bool IsEntryReadFailure(Exception ex) => ex is LimitExceededException or InvalidDataException or IOException;

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

}
