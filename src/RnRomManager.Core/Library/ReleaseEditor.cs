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

/// <summary>Release edits of a game (delete, set primary, edit, move, merge), for <see cref="LibraryEditor"/>.</summary>
internal sealed class ReleaseEditor(
    LibraryEditSupport libraryEditSupport,
    GameRemover gameRemover,
    ExternalReleaseEditor externalReleaseEditor,
    JobLock jobLock,
    IGameRepository games,
    IRomRepository roms,
    ILibraryMirror mirror,
    InfoJsonReader reader,
    ISystemRegistry systems,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    AuditPaths auditPaths,
    TimeProvider time,
    ILogger<LibraryEditor> logger)
{
    internal async Task<LibraryEditResult> DeleteReleaseAsync(
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
        if (libraryEditSupport.LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = libraryEditSupport.OpenForEdit(row);
        if (target.Code is not null)
            return new(target.Code, target.Details);

        using var existing = target.Zip!;
        var current = target.Game!;
        // The DB row only finds the release; the ZIP's info.json decides what's there (§6.1).
        if (current.Roms.FirstOrDefault(r => r.Metadata.Sha1 == romRow.Sha1) is not { } rom)
            return new(NotFound, $"{row.ZipPath} no longer lists SHA1 {romRow.Sha1}");

        var remaining = current.Roms.Where(r => !ReferenceEquals(r, rom)).ToList();
        if (remaining.Count == 0)
            return await gameRemover.RemoveGameAsync(existing, row, current, actor).ConfigureAwait(false); // the last release removes the game
        var rebuilt = current with { Roms = PrimaryRelease.Apply(remaining, libraryEditSupport.DatSourceOf(row.SystemId), current.ChosenPrimarySha1), Metadata = libraryEditSupport.Touched(current) };
        if (libraryEditSupport.IsExternal(row))
        {
            var external = await gameRemover.RemoveExternalAsync(existing, row, [rom], rebuilt, actor).ConfigureAwait(false);
            if (external.Code is null)
                logger.LogInformation("Release {FileName} removed from game {GameId} ({ZipPath})", rom.FileName, gameId, row.ZipPath);
            return external;
        }
        var (result, copies) = await gameRemover.RemoveRomsAsync(existing, current, [rom], () => libraryEditSupport.WriteAsync(existing, row.ZipPath, rebuilt)).ConfigureAwait(false);
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
    internal async Task<LibraryEditResult> SetPrimaryReleaseAsync(
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
        if (libraryEditSupport.LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = libraryEditSupport.OpenForEdit(row);
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
            Metadata = libraryEditSupport.Touched(current),
        };
        var result = await libraryEditSupport.WriteAsync(existing, row.ZipPath, game).ConfigureAwait(false);
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
    internal async Task<LibraryEditResult> EditReleaseAsync(
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
        if (libraryEditSupport.LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = libraryEditSupport.OpenForEdit(row);
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
            name = libraryEditSupport.IsExternal(row)
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
            if (!libraryEditSupport.IsExternal(row))
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
            Roms = PrimaryRelease.Apply([.. current.Roms.Select(r => ReferenceEquals(r, rom) ? edited : r)], libraryEditSupport.DatSourceOf(row.SystemId), chosen),
            Metadata = libraryEditSupport.Touched(current),
        };
        var result = await libraryEditSupport.WriteAsync(existing, row.ZipPath, game, entries, removed, saves).ConfigureAwait(false);
        if (LibraryEditSupport.FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.ReleaseEdited, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
                Details: name == rom.FileName ? rom.FileName : $"{rom.FileName} → {name}"));
        if (result.Code is null)
            logger.LogInformation("Release {FileName} of game {GameId} edited", rom.FileName, gameId);
        return result with { FileName = name };
    }

    /// <summary>DLC B1: <c>Update</c> wins over <c>DLC</c>.</summary>
    internal static string KindOf(IReadOnlyList<string> flags) =>
        HasFlag(flags, ReleaseVersion.UpdateFlag) ? ReleaseEdit.KindUpdate : HasFlag(flags, "DLC") ? ReleaseEdit.KindDlc : ReleaseEdit.KindRelease;

    internal static bool HasFlag(IReadOnlyList<string> flags, string flag) => flags.Contains(flag, StringComparer.OrdinalIgnoreCase);

    internal static ReleaseTags Tags(InfoJsonRom rom) =>
        new(rom.Regions, rom.Languages, rom.Revision, HasFlag(rom.Flags, "Hack"), HasFlag(rom.Flags, "Translated"), rom.Version);

    /// <summary>B5: flags keep their order; a flag turned on is appended, a changed disc replaces its flag in place.</summary>
    internal static List<string> EditedFlags(IReadOnlyList<string> flags, ReleaseEdit patch, string kind)
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

    /// <summary>
    /// §11 move release, to an existing game (<paramref name="targetGameId"/>) or a new one (<paramref name="newGameTitle"/>,
    /// which runs §7.2 steps 7–8 for it). The release keeps its <c>baseTitle</c> (D23). Order: target ZIP (now holding the
    /// ROM), source ZIP rebuilt, one DB transaction, then an emptied source ZIP is deleted.
    /// </summary>
    internal async Task<LibraryEditResult> MoveReleaseAsync(
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
        if ((libraryEditSupport.LegacyRefusal(row) ?? (targetRow is null ? null : libraryEditSupport.LegacyRefusal(targetRow))) is { } legacy)
            return legacy;
        if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            return await externalReleaseEditor.MoveReleaseExternalAsync(row, romRow, targetRow, newGameTitle, actor).ConfigureAwait(false);

        var source = libraryEditSupport.OpenForEdit(row);
        if (source.Code is not null)
            return new(source.Code, source.Details);
        using var sourceZip = source.Zip!;
        if (source.Game!.Roms.FirstOrDefault(r => r.Metadata.Sha1 == romRow.Sha1) is not { } rom)
            return new(NotFound, $"{row.ZipPath} no longer lists SHA1 {romRow.Sha1}");
        var target = targetRow is null ? null : libraryEditSupport.OpenForEdit(targetRow);
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
            targetGame = await libraryEditSupport.NewGameForAsync(row.SystemId, rom, newGameTitle!, PrimaryRelease.Apply([moved], libraryEditSupport.DatSourceOf(row.SystemId)), entries, now, ct)
                .ConfigureAwait(false);
            try
            {
                targetPath = libraryEditSupport.NewZipPath(row.SystemId, newGameTitle!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(IoError, ex.Message);
            }
        }
        else
        {
            targetGame = target.Game! with { Roms = PrimaryRelease.Apply([.. target.Game.Roms, moved], libraryEditSupport.DatSourceOf(row.SystemId), target.Game.ChosenPrimarySha1), Metadata = libraryEditSupport.Touched(target.Game) };
            targetPath = targetRow!.ZipPath;
        }

        var remaining = source.Game.Roms.Where(r => !ReferenceEquals(r, rom)).ToList();
        var sourceGame = remaining.Count == 0 ? null : source.Game with { Roms = PrimaryRelease.Apply(remaining, libraryEditSupport.DatSourceOf(row.SystemId), source.Game.ChosenPrimarySha1), Metadata = libraryEditSupport.Touched(source.Game) };
        // B5: the last release deletes the source game, so its saves go with it into the target.
        var (savesError, saves) = sourceGame is null ? LibraryEditSupport.CarrySaves(sourceZip, targetZip, LibraryEditSupport.Renames((rom.FileName, moved.FileName))) : (null, null);
        if (savesError is not null)
            return savesError;

        if (await libraryEditSupport.WriteZipAsync(targetZip, targetPath, targetGame, entries, removed, saves).ConfigureAwait(false) is { } failed)
            return failed;
        if (sourceGame is not null && await libraryEditSupport.WriteZipAsync(sourceZip, row.ZipPath, sourceGame, []).ConfigureAwait(false) is { } sourceFailed)
        {
            await UndoTargetWriteAsync(targetPath, target?.Game).ConfigureAwait(false);
            return sourceFailed;
        }
        audit.Record(new AuditEvent(AuditActions.ReleaseMoved, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
            AuditPaths.Library(targetPath), rom.FileName));

        var result = await libraryEditSupport.MirrorAsync(() => mirror.ApplyAsync(
            [libraryEditSupport.Mirror(targetPath, targetGame), .. sourceGame is null ? Array.Empty<MirrorEntry>() : [libraryEditSupport.Mirror(row.ZipPath, sourceGame)]],
            sourceGame is null ? [gameId] : [], CancellationToken.None), targetPath).ConfigureAwait(false);
        if (sourceGame is null)
        {
            sourceZip.Dispose(); // Windows can't delete an open file.
            libraryEditSupport.DeleteOldZip(row.ZipPath, gameId);
        }
        if (result.Code is null)
        {
            logger.LogInformation("Release {FileName} moved from game {GameId} to {TargetId} ({ZipPath})", rom.FileName, gameId, targetGame.GameId, targetPath);
            if (sourceGame is null)
                await libraryEditSupport.MoveCollectionMembershipsAsync(gameId, targetGame.GameId).ConfigureAwait(false);
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
    internal async Task<LibraryEditResult> MergeAsync(
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
        if ((libraryEditSupport.LegacyRefusal(row) ?? libraryEditSupport.LegacyRefusal(targetRow)) is { } legacy)
            return legacy;
        if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            return await externalReleaseEditor.MergeExternalAsync(row, targetRow, actor).ConfigureAwait(false);

        var source = libraryEditSupport.OpenForEdit(row);
        if (source.Code is not null)
            return new(source.Code, source.Details);
        using var sourceZip = source.Zip!;
        var target = libraryEditSupport.OpenForEdit(targetRow);
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

        var targetGame = libraryEditSupport.MergedInto(into, from, merged, images);
        var (savesError, saves) = LibraryEditSupport.CarrySaves(sourceZip, targetZip, renames);
        if (savesError is not null)
            return savesError;

        if (await libraryEditSupport.WriteZipAsync(targetZip, targetRow.ZipPath, targetGame, entries, removed, saves).ConfigureAwait(false) is { } failed)
            return failed;
        audit.Record(new AuditEvent(AuditActions.GamesMerged, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
            AuditPaths.Library(targetRow.ZipPath), $"{from.Roms.Count} releases, {copies.Count} images"));
        var result = await libraryEditSupport.MirrorAsync(() => mirror.ApplyAsync([libraryEditSupport.Mirror(targetRow.ZipPath, targetGame)], [gameId], CancellationToken.None),
            targetRow.ZipPath).ConfigureAwait(false);
        sourceZip.Dispose();
        libraryEditSupport.DeleteOldZip(row.ZipPath, gameId);
        if (result.Code is null)
        {
            logger.LogInformation("Game {GameId} merged into {TargetId} ({ZipPath})", gameId, targetGameId, targetRow.ZipPath);
            await libraryEditSupport.MoveCollectionMembershipsAsync(gameId, targetGameId).ConfigureAwait(false);
        }
        await libraryEditSupport.ClearIgdbSyncAsync(into, targetGame, result).ConfigureAwait(false);
        return result with { GameId = targetGameId };
    }

    /// <summary>A failed source rebuild after the target was written: put the target back, so the ROM isn't in two ZIPs.</summary>
    internal async Task UndoTargetWriteAsync(string targetPath, InfoJsonGame? before)
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
            await libraryEditSupport.Writer(before.System).WriteAsync(path, LibraryZip.Open(path, reader),
                new LibraryZipChanges(before, new Dictionary<string, Func<Stream>>())).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Couldn't undo the write of {ZipPath}; the moved ROM is in two ZIPs until one is fixed by hand", targetPath);
        }
    }
}
