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

/// <summary>Helpers shared by <see cref="LibraryEditor"/> and its editors: ZIP writes, the DB mirror, legacy refusals and external file moves. Calls none of the editors.</summary>
internal sealed class LibraryEditSupport(
    IGameRepository games,
    ILibraryMirror mirror,
    InfoJsonReader reader,
    DatCache dats,
    ISystemRegistry systems,
    MediaService media,
    IOptions<PathsOptions> paths,
    ICollectionRepository collections,
    TimeProvider time,
    ILogger<LibraryEditor> logger,
    IRomStorageSettings? zipSettings)
{
    /// <summary>Z4: a writer at the game's system ZIP compression level.</summary>
    internal LibraryZipWriter Writer(string systemId) => new(reader, zipSettings?.Compression(systemId) ?? ZipCompression.Med);

    /// <summary>The ZIP was written (or deleted), so an audit row is due even when the DB mirror then failed (AU4).</summary>
    internal static bool FilesChanged(LibraryEditResult result) => result.Code is null or DbWriteFailed;

    /// <summary>A6: a changed IGDB link drops <c>actions</c>, so both Admin actions pick the game up again.</summary>
    internal static InfoJsonGame Relinked(InfoJsonGame before, InfoJsonGame after) =>
        after.IgdbId == before.IgdbId ? after : after with { Actions = null };

    /// <summary>A6: once the mirror succeeded, a changed IGDB link clears the game's IGDB rating columns; a failure only warns.</summary>
    internal async Task ClearIgdbSyncAsync(InfoJsonGame before, InfoJsonGame after, LibraryEditResult result)
    {
        if (result.Code is not null || before.IgdbId == after.IgdbId)
            return;
        try
        {
            await games.ClearIgdbSyncAsync(after.GameId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Clearing the IGDB sync of game {GameId} failed", after.GameId);
        }
    }

    /// <summary>Test seams of the external edit path: points where a test makes a move or the ZIP write fail.</summary>
    internal ExternalEditHooks? ExternalHooks { get; set; }

    /// <summary>A failed checked write, after its undo: <c>VerifyFailed</c> or <c>IoError</c>; anything else is rethrown as it was.</summary>
    internal static LibraryEditResult WriteFailure(Exception ex)
    {
        if (ex is LibraryZipVerifyException)
            return new(VerifyFailed, ex.Message);
        if (ex is IOException or UnauthorizedAccessException)
            return new(IoError, ex.Message);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex);
        throw new System.Diagnostics.UnreachableException();
    }

    /// <summary>
    /// The new game a moved release starts: §7.2 step 7 (all metadata from the release's DAT entry) and step 8 (its
    /// media, whose bytes are added to <paramref name="entries"/>).
    /// </summary>
    internal async Task<InfoJsonGame> NewGameForAsync(
        string systemId,
        InfoJsonRom rom,
        string title,
        IReadOnlyList<InfoJsonRom> roms,
        Dictionary<string, Func<Stream>> entries,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var metadata = MetadataFor(systemId, rom);
        var found = await media.FindAsync(systems.ById(systemId)!, [rom.Identification.DatGameName], new CircuitBreaker(), ct).ConfigureAwait(false);
        foreach (var image in new[] { found.BoxArt, found.Snap }.OfType<MediaImage>())
            entries[LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(image.Bytes);
        return new InfoJsonGame
        {
            SchemaVersion = InfoJsonReader.CurrentVersion,
            GameId = Guid.NewGuid(),
            System = systemId,
            Title = title,
            DisplayTitle = DisplayTitle.From(title),
            ReleaseYear = metadata.ReleaseYear,
            Developers = metadata.Developers,
            Publishers = metadata.Publishers,
            Genres = metadata.Genres,
            Description = null,
            Rating = null,
            Favourite = false,
            Roms = roms,
            Images = GameImages.ForNewGame(found, now),
            Metadata = new InfoJsonGameMetadata(now, now, AppInfo.Version),
        };
    }

    /// <summary>I4a: the target keeps its title, metadata and cover; empty fields are filled from the source.</summary>
    internal InfoJsonGame MergedInto(
        InfoJsonGame into,
        InfoJsonGame from,
        List<InfoJsonRom> roms,
        IReadOnlyList<InfoJsonImage> images) => Relinked(into, into with
    {
        ReleaseYear = into.ReleaseYear ?? from.ReleaseYear,
        Developers = into.Developers.Count > 0 ? into.Developers : from.Developers,
        Publishers = into.Publishers.Count > 0 ? into.Publishers : from.Publishers,
        Genres = into.Genres.Count > 0 ? into.Genres : from.Genres,
        Description = string.IsNullOrEmpty(into.Description) ? from.Description : into.Description,
        Rating = into.Rating ?? from.Rating,
        Favourite = into.Favourite || from.Favourite,
        IgdbId = into.IgdbId ?? from.IgdbId,
        ChosenPrimarySha1 = into.ChosenPrimarySha1 ?? from.ChosenPrimarySha1,
        Roms = PrimaryRelease.Apply(roms, DatSourceOf(into.System), into.ChosenPrimarySha1 ?? from.ChosenPrimarySha1),
        Images = images,
        Metadata = Touched(into),
    });

    /// <summary>
    /// B18: the deleted source game's collection memberships follow it to the target. The library change has already
    /// committed, so a failure is only logged.
    /// </summary>
    internal async Task MoveCollectionMembershipsAsync(Guid fromGameId, Guid toGameId)
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
    internal static void CarryNote(
        LibraryZip from,
        string fromName,
        string toName,
        Dictionary<string, Func<Stream>> entries,
        HashSet<string> removed)
    {
        var (note, target) = (LibraryZip.NoteEntry(fromName), LibraryZip.NoteEntry(toName));
        if (from.HasEntry(note))
            entries[target] = () => from.OpenEntry(note, LibraryZip.MaxNoteBytes);
        else
            removed.Add(target);
    }

    /// <summary>§7.2 step 7: metadat by the CRC of the DAT entry the release is identified with (Unverified: the chosen entry).</summary>
    internal DatMetadata MetadataFor(string systemId, InfoJsonRom rom)
    {
        if (rom.Identification.DatGameName is not { } name || dats.For(systemId) is not { } store)
            return DatMetadata.Empty;
        // T7: a titledb release finds its base entry by the id its datGameName ends with.
        if (systems.ById(systemId) is { DatSource: DatSource.TitleDb })
            return TitleDb.TitleIdOfGameName(name) is { } titleId && store.ByTitleId(titleId) is { } baseEntry
                ? TitleDb.MetadataOf(baseEntry) : DatMetadata.Empty;
        // Arcade G4: by the set name kept in the header (a Manual release has none).
        if (systems.ById(systemId)?.DatSource.IsRomset() == true)
            return rom.Header?["setName"] is System.Text.Json.Nodes.JsonValue setName && setName.TryGetValue<string>(out var set) ? store.MetadataForSet(set) : DatMetadata.Empty;
        var entry = rom.Identification.Method == IdentificationMethod.Dat
            ? store.BySha1(rom.Metadata.Sha1)
            : store.Roms.FirstOrDefault(r => r.GameName == name);
        return entry is null ? DatMetadata.Empty : store.MetadataFor(entry.Crc32);
    }

    /// <summary>B5: a release's name before → after a merge or move, the same pairs <see cref="CarryNote"/> gets (ignoring case).</summary>
    internal static Dictionary<string, string> Renames(params (string From, string To)[] pairs) =>
        pairs.ToDictionary(p => p.From, p => p.To, StringComparer.OrdinalIgnoreCase);

    /// <summary>§6.1 entry name clash: ` (2)`… before the extension, case-insensitively.</summary>
    internal static string FreeName(string fileName, IEnumerable<string> taken) =>
        FileNameRules.FirstFree(fileName, taken.ToHashSet(StringComparer.OrdinalIgnoreCase).Contains);

    internal DatSource DatSourceOf(string systemId) => systems.ById(systemId)?.DatSource ?? DatSource.Libretro;

    internal bool IsExternal(GameRow row) => systems.ById(row.SystemId) is { EmbedRomsInZip: false };

    /// <summary>
    /// S3: <c>StorageMigrationPending</c> for a legacy game (its <c>info.json</c> keeps every ROM in the ZIP of a
    /// <c>LegacyEmbeddedRoms</c> system), checked before any other conflict. Only such systems' ZIPs are opened; an
    /// unreadable one is left to the write's own open to report.
    /// </summary>
    internal LibraryEditResult? LegacyRefusal(GameRow row)
    {
        if (systems.ById(row.SystemId) is not { LegacyEmbeddedRoms: true } handler)
            return null;
        var target = OpenForEdit(row);
        using var zip = target.Zip;
        return target.Game is { } game && RomStorage.IsPending(handler, game) ? new(StorageMigrationPending, StorageMigrationPendingTitle) : null;
    }

    /// <summary>The game's ZIP; an external game's through a checked handle that must be the mirrored game (G4, I4).</summary>
    internal LibraryTarget OpenForEdit(GameRow row) =>
        LibraryTarget.OpenForWrite(paths.Value.Roms, row.ZipPath, reader, systems, row.Id, row.SystemId);

    /// <summary>Atomic ZIP write (§6.1) then the DB mirror. Not cancellable once started, so a ZIP is never left half-applied.</summary>
    internal async Task<LibraryEditResult> WriteAsync(
        LibraryZip existing,
        string zipPath,
        InfoJsonGame game,
        Dictionary<string, Func<Stream>>? newEntries = null,
        IReadOnlySet<string>? removedEntries = null,
        SavesChange? saves = null) =>
        await WriteZipAsync(existing, zipPath, game, newEntries ?? [], removedEntries, saves).ConfigureAwait(false)
        ?? await MirrorAsync(() => mirror.ReplaceAsync(Mirror(zipPath, game), CancellationToken.None), zipPath).ConfigureAwait(false);

    /// <param name="existing">The game's current ZIP (disposed by the writer), or null for a new game.</param>
    /// <returns>null when written and verified.</returns>
    internal async Task<LibraryEditResult?> WriteZipAsync(
        LibraryZip? existing,
        string zipPath,
        InfoJsonGame game,
        Dictionary<string, Func<Stream>> newEntries,
        IReadOnlySet<string>? removedEntries = null,
        SavesChange? saves = null)
    {
        try
        {
            var path = SafePath.Resolve(paths.Value.Roms, zipPath);
            var changes = new LibraryZipChanges(game, newEntries, removedEntries, Saves: saves);
            // G4: a ZIP opened through a checked handle is replaced only through the checked write.
            if (LibraryTarget.NeedsCheck(systems, zipPath, game.System))
                await Writer(game.System).WriteCheckedAsync(paths.Value.Roms, path, existing, changes).ConfigureAwait(false);
            else
                await Writer(game.System).WriteAsync(path, existing, changes).ConfigureAwait(false);
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

    internal MirrorEntry Mirror(string zipPath, InfoJsonGame game)
    {
        var info = new FileInfo(SafePath.Resolve(paths.Value.Roms, zipPath));
        return new MirrorEntry(game, zipPath, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>§6.1: <c>Sanitize(title).zip</c> in <c>roms/&lt;system&gt;/&lt;bucket&gt;/</c>, ` (2)`… on a clash (any case).</summary>
    internal string NewZipPath(string systemId, string title)
    {
        var zipName = FileNameSanitizer.Sanitize(title) + ".zip";
        var folder = Path.Combine(paths.Value.Roms, systemId, Bucket.For(zipName));
        Directory.CreateDirectory(folder);
        zipName = FileNameRules.FirstFreeIn(folder, zipName);
        return $"{systemId}/{Bucket.For(zipName)}/{zipName}";
    }

    /// <summary>The ZIP already changed; a DB failure is healed later by the ingest pre-pass or integrity (§14.2).</summary>
    internal async Task<LibraryEditResult> MirrorAsync(Func<Task> write, string zipPath)
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
    internal void DeleteOldZip(string zipPath, Guid gameId)
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

    internal InfoJsonGameMetadata Touched(InfoJsonGame current) =>
        current.Metadata with { UpdatedAt = time.GetUtcNow(), AppVersion = AppInfo.Version };

    /// <summary>W6: moves the files of a failed edit back, newest first; a move that fails is logged and left.</summary>
    /// <param name="movedRoot">The root the files were moved under, when it isn't the library (<c>_removed/</c>).</param>
    internal void UndoMoves(List<(string From, string To, ExternalFileSnapshot Snapshot)> placed, string? movedRoot = null)
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
    internal void DeleteOldZipIfUnchanged(string zipPath, ExternalFileSnapshot opened, Guid gameId)
    {
        var path = SafePath.Resolve(paths.Value.Roms, zipPath);
        if (ExternalRomPath.ClassifyFile(paths.Value.Roms, path) is not { Status: ExternalPathStatus.Found } current || current.Snapshot != opened)
        {
            logger.LogError("Game {GameId} moved away from {ZipPath}, but what is there now isn't the ZIP it left; it was kept", gameId, zipPath);
            return;
        }
        DeleteOldZip(zipPath, gameId);
    }

    internal bool StillPublished(string fullPath, ExternalFileSnapshot published) =>
        ExternalRomPath.ClassifyFile(paths.Value.Roms, fullPath) is { Status: ExternalPathStatus.Found } current && current.Snapshot == published;

    /// <summary>
    /// B5: the target write's saves when a merge or a last-release move carries the source's saves: the target's records,
    /// then the source's (a clashing id gets a new one), each source record's <c>release</c> renamed as its release was
    /// (<paramref name="renames"/>, matched ignoring case; an orphan stays as it is), their bytes streamed from the source.
    /// </summary>
    /// <param name="target">The target's ZIP; null for a new game.</param>
    /// <returns><c>SavesUnreadable</c> (either side) or <c>SavesIndexFull</c>; else the change, null when the source has no saves.</returns>
    internal static (LibraryEditResult? Error, SavesChange? Change) CarrySaves(LibraryZip source, LibraryZip? target,
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
        var removed = new HashSet<string>(StringComparer.Ordinal);
        // B18: the target's own records are upgraded from the target ZIP (for a B9 rename the source is the target).
        records = [.. GameSaves.Upgrade(records, s => () => (target ?? source).OpenEntry(s.Entry, s.SizeBytes), added, removed)];
        var carriedRecords = new List<GameSave>();
        foreach (var save in from.Index!.Saves)
        {
            var carried = ids.Add(save.Id) ? save : save with { Id = GameSaves.NewId() };
            ids.Add(carried.Id);
            if (renames.TryGetValue(save.Release, out var renamed))
                carried = carried with { Release = renamed };
            added[carried.Entry] = () => source.OpenEntry(save.Entry, save.SizeBytes);
            carriedRecords.Add(carried);
        }
        // A carried legacy record's bytes are already in `added` under its legacy name, so Upgrade re-keys them.
        records.AddRange(GameSaves.Upgrade(carriedRecords, s => () => source.OpenEntry(s.Entry, s.SizeBytes), added, removed));
        // Only the target's own entries can be removed; a carried one never was in it.
        removed.IntersectWith((target ?? source).EntryNames);
        return GameSaves.Serialize(records) is { } index
            ? (null, new SavesChange(index, added, removed))
            : (new(SavesIndexFull, SavesIndexFullTitle), null);
    }
}
