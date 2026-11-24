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

/// <summary>
/// Admin library edits (spec §11, §23, D28, D35): metadata PATCH, delete release/game, move release, merge, retry media
/// (§10.2). Each takes the global job lock
/// (§3.1) and follows the sync rule (§14.2): the ZIP is written (or deleted) first, then the DB. Every write starts from
/// the ZIP's own <c>info.json</c> (§6.1). Removed ROM bytes go to <c>ingest/&lt;system&gt;/_removed/</c>, never deleted.
/// External-storage paths (W2–W5) are in <c>LibraryEditor.External.cs</c>.
/// </summary>
public sealed partial class LibraryEditor(JobLock jobLock, IGameRepository games, IRomRepository roms, IImageRepository images, ILibraryMirror mirror,
    InfoJsonReader reader, DatCache dats, ISystemRegistry systems, MediaService media, GameMediaRetry mediaRetry,
    IOptions<PathsOptions> paths, IAuditLog audit, AuditPaths auditPaths, ICollectionRepository collections, TimeProvider time,
    ILogger<LibraryEditor> logger, IRomStorageSettings? zipSettings = null)
{
    /// <summary>Z4: a writer at the game's system ZIP compression level.</summary>
    private LibraryZipWriter Writer(string systemId) => new(reader, zipSettings?.Compression(systemId) ?? ZipCompression.Med);
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
        if (LegacyRefusal(row) is { } legacy)
            return legacy;
        if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            return await PatchExternalAsync(row, patch, actor).ConfigureAwait(false);
        var target = OpenForEdit(row);
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
            IgdbId = patch.SetIgdbId ? patch.IgdbId : current.IgdbId,
            Metadata = Touched(current),
        };
    }

    /// <summary>Test seams of the external edit path: points where a test makes a move or the ZIP write fail.</summary>
    public ExternalEditHooks? ExternalHooks { get; set; }

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
        if (LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = OpenForEdit(row);
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
        if (LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = OpenForEdit(row);
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
        var rebuilt = current with { Roms = PrimaryRelease.Apply(remaining, DatSourceOf(row.SystemId), current.ChosenPrimarySha1), Metadata = Touched(current) };
        if (IsExternal(row))
        {
            var external = await RemoveExternalAsync(existing, row, [rom], rebuilt, actor).ConfigureAwait(false);
            if (external.Code is null)
                logger.LogInformation("Release {FileName} removed from game {GameId} ({ZipPath})", rom.FileName, gameId, row.ZipPath);
            return external;
        }
        var (result, copies) = await RemoveRomsAsync(existing, current, [rom], () => WriteAsync(existing, row.ZipPath, rebuilt)).ConfigureAwait(false);
        if (FilesChanged(result))
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
    public async Task<LibraryEditResult> SetPrimaryReleaseAsync(Guid gameId, long romId, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        if (await roms.GetAsync(gameId, romId, ct).ConfigureAwait(false) is not { } romRow
            || await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        if (LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = OpenForEdit(row);
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
            Metadata = Touched(current),
        };
        var result = await WriteAsync(existing, row.ZipPath, game).ConfigureAwait(false);
        if (FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.PrimarySet, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath), Details: rom.FileName));
        if (result.Code is null)
            logger.LogInformation("Release {FileName} is now the primary of game {GameId}", rom.FileName, gameId);
        return result;
    }

    /// <summary>
    /// B5–B10: changes a release's tags and renames it to match (B7), except on a <c>FileSets</c> system. The checks that
    /// need the game (disc on a non-disc system, unknown regions, a version on a non-update) come back as <c>ValidationFailed</c>.
    /// </summary>
    public async Task<LibraryEditResult> EditReleaseAsync(Guid gameId, long romId, ReleaseEdit patch, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        if (await roms.GetAsync(gameId, romId, ct).ConfigureAwait(false) is not { } romRow
            || await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        if (LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = OpenForEdit(row);
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
            name = IsExternal(row)
                ? ExternalRomName.AllocateLogicalName(name, new ExternalNameReservations(others))
                : FreeName(name, others);
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
                var (savesError, change) = CarrySaves(existing, null, Renames((rom.FileName, name)));
                if (savesError is not null)
                    return savesError;
                saves = change;
            }
            if (!IsExternal(row))
            {
                entries[LibraryZip.RomEntry(name)] = () => existing.OpenEntry(LibraryZip.RomEntry(rom.FileName), rom.Metadata.SizeBytes);
                removed.Add(LibraryZip.RomEntry(rom.FileName));
            }
            CarryNote(existing, rom.FileName, name, entries, removed);
        }

        // An add-on is never kept as an Admin-chosen primary.
        var chosen = current.ChosenPrimarySha1 == rom.Metadata.Sha1 && kind != ReleaseEdit.KindRelease ? null : current.ChosenPrimarySha1;
        var game = current with
        {
            ChosenPrimarySha1 = chosen,
            Roms = PrimaryRelease.Apply([.. current.Roms.Select(r => ReferenceEquals(r, rom) ? edited : r)], DatSourceOf(row.SystemId), chosen),
            Metadata = Touched(current),
        };
        var result = await WriteAsync(existing, row.ZipPath, game, entries, removed, saves).ConfigureAwait(false);
        if (FilesChanged(result))
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
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(JobRunning);
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        if (LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = OpenForEdit(row);
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
        if (LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = OpenForEdit(row);
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
        if (result.Code is not null)
            return result;
        logger.LogInformation("Image {FileName} ({Type}) uploaded to game {GameId} ({ZipPath})", image.FileName, type, gameId, row.ZipPath);
        return placeholder is { IsCover: true } ? result with { ReplacedDefaultCover = true } : result;
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
        if (LegacyRefusal(row) is { } legacy)
            return (legacy, null, null, null, null);
        var target = OpenForEdit(row);
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
        if (LegacyRefusal(row) is { } legacy)
            return legacy;
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
        if ((LegacyRefusal(row) ?? (targetRow is null ? null : LegacyRefusal(targetRow))) is { } legacy)
            return legacy;
        if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            return await MoveReleaseExternalAsync(row, romRow, targetRow, newGameTitle, actor).ConfigureAwait(false);

        var source = OpenForEdit(row);
        if (source.Code is not null)
            return new(source.Code, source.Details);
        using var sourceZip = source.Zip!;
        if (source.Game!.Roms.FirstOrDefault(r => r.Metadata.Sha1 == romRow.Sha1) is not { } rom)
            return new(NotFound, $"{row.ZipPath} no longer lists SHA1 {romRow.Sha1}");
        var target = targetRow is null ? null : OpenForEdit(targetRow);
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
            targetGame = await NewGameForAsync(row.SystemId, rom, newGameTitle!, PrimaryRelease.Apply([moved], DatSourceOf(row.SystemId)), entries, now, ct)
                .ConfigureAwait(false);
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
            targetGame = target.Game! with { Roms = PrimaryRelease.Apply([.. target.Game.Roms, moved], DatSourceOf(row.SystemId), target.Game.ChosenPrimarySha1), Metadata = Touched(target.Game) };
            targetPath = targetRow!.ZipPath;
        }

        var remaining = source.Game.Roms.Where(r => !ReferenceEquals(r, rom)).ToList();
        var sourceGame = remaining.Count == 0 ? null : source.Game with { Roms = PrimaryRelease.Apply(remaining, DatSourceOf(row.SystemId), source.Game.ChosenPrimarySha1), Metadata = Touched(source.Game) };
        // B5: the last release deletes the source game, so its saves go with it into the target.
        var (savesError, saves) = sourceGame is null ? CarrySaves(sourceZip, targetZip, Renames((rom.FileName, moved.FileName))) : (null, null);
        if (savesError is not null)
            return savesError;

        if (await WriteZipAsync(targetZip, targetPath, targetGame, entries, removed, saves).ConfigureAwait(false) is { } failed)
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

    /// <summary>A failed checked write, after its undo: <c>VerifyFailed</c> or <c>IoError</c>; anything else is rethrown as it was.</summary>
    private static LibraryEditResult WriteFailure(Exception ex)
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
    private async Task<InfoJsonGame> NewGameForAsync(string systemId, InfoJsonRom rom, string title, IReadOnlyList<InfoJsonRom> roms,
        Dictionary<string, Func<Stream>> entries, DateTimeOffset now, CancellationToken ct)
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
        if ((LegacyRefusal(row) ?? LegacyRefusal(targetRow)) is { } legacy)
            return legacy;
        if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            return await MergeExternalAsync(row, targetRow, actor).ConfigureAwait(false);

        var source = OpenForEdit(row);
        if (source.Code is not null)
            return new(source.Code, source.Details);
        using var sourceZip = source.Zip!;
        var target = OpenForEdit(targetRow);
        if (target.Code is not null)
            return new(target.Code, target.Details);
        using var targetZip = target.Zip!;
        var (from, into) = (source.Game!, target.Game!);

        var entries = new Dictionary<string, Func<Stream>>();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var names = into.Roms.Select(r => r.FileName).ToList();
        var merged = new List<InfoJsonRom>(into.Roms);
        var renames = Renames();
        foreach (var rom in from.Roms)
        {
            var moved = rom with { FileName = FreeName(rom.FileName, names) };
            renames[rom.FileName] = moved.FileName;
            names.Add(moved.FileName);
            merged.Add(moved);
            entries[LibraryZip.RomEntry(moved.FileName)] = () => sourceZip.OpenEntry(LibraryZip.RomEntry(rom.FileName), rom.Metadata.SizeBytes);
            CarryNote(sourceZip, rom.FileName, moved.FileName, entries, removed);
        }

        if (GameImages.Merge(into.Images, from.Images) is not var (images, copies))
            return new(TooManyImages, $"The merged game would have more than {GameImages.MaxImages} images.");
        foreach (var (copy, sourceName) in copies)
            entries[LibraryZip.ImageEntry(copy.FileName)] = () => sourceZip.OpenEntry(LibraryZip.ImageEntry(sourceName), copy.SizeBytes);

        var targetGame = MergedInto(into, from, merged, images);
        var (savesError, saves) = CarrySaves(sourceZip, targetZip, renames);
        if (savesError is not null)
            return savesError;

        if (await WriteZipAsync(targetZip, targetRow.ZipPath, targetGame, entries, removed, saves).ConfigureAwait(false) is { } failed)
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

    /// <summary>I4a: the target keeps its title, metadata and cover; empty fields are filled from the source.</summary>
    private InfoJsonGame MergedInto(InfoJsonGame into, InfoJsonGame from, List<InfoJsonRom> roms, IReadOnlyList<InfoJsonImage> images) => into with
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
    };

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
            await Writer(before.System).WriteAsync(path, LibraryZip.Open(path, reader),
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
        // T7: a titledb release finds its base entry by the id its datGameName ends with.
        if (systems.ById(systemId) is { DatSource: DatSource.TitleDb })
            return TitleDb.TitleIdOfGameName(name) is { } titleId && store.ByTitleId(titleId) is { } baseEntry
                ? TitleDb.MetadataOf(baseEntry) : DatMetadata.Empty;
        var entry = rom.Identification.Method == IdentificationMethod.Dat
            ? store.BySha1(rom.Metadata.Sha1)
            : store.Roms.FirstOrDefault(r => r.GameName == name);
        return entry is null ? DatMetadata.Empty : store.MetadataFor(entry.Crc32);
    }

    /// <summary>B5: a release's name before → after a merge or move, the same pairs <see cref="CarryNote"/> gets (ignoring case).</summary>
    private static Dictionary<string, string> Renames(params (string From, string To)[] pairs) =>
        pairs.ToDictionary(p => p.From, p => p.To, StringComparer.OrdinalIgnoreCase);

    /// <summary>§6.1 entry name clash: ` (2)`… before the extension, case-insensitively.</summary>
    private static string FreeName(string fileName, IEnumerable<string> taken) =>
        FileNameRules.FirstFree(fileName, taken.ToHashSet(StringComparer.OrdinalIgnoreCase).Contains);

    /// <summary>Deletes the game's ZIP (its ROMs copied to <c>_removed/</c> first) and records <c>GameDeleted</c>.</summary>
    private async Task<LibraryEditResult> RemoveGameAsync(LibraryZip existing, GameRow row, InfoJsonGame current, AuditActor actor)
    {
        if (IsExternal(row))
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
            return await MirrorAsync(() => mirror.DeleteAsync(row.Id, CancellationToken.None), row.ZipPath).ConfigureAwait(false);
        }).ConfigureAwait(false);
        // Only the ROM entries went to _removed/; images and info.json were discarded.
        if (FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.GameDeleted, actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                $"ingest/{row.SystemId}/{StorageFolders.RemovedFolder}/"));
        return result;
    }

    private DatSource DatSourceOf(string systemId) => systems.ById(systemId)?.DatSource ?? DatSource.Libretro;

    private bool IsExternal(GameRow row) => systems.ById(row.SystemId) is { EmbedRomsInZip: false };

    /// <summary>
    /// S3: <c>StorageMigrationPending</c> for a legacy game (its <c>info.json</c> keeps every ROM in the ZIP of a
    /// <c>LegacyEmbeddedRoms</c> system), checked before any other conflict. Only such systems' ZIPs are opened; an
    /// unreadable one is left to the write's own open to report.
    /// </summary>
    private LibraryEditResult? LegacyRefusal(GameRow row)
    {
        if (systems.ById(row.SystemId) is not { LegacyEmbeddedRoms: true } handler)
            return null;
        var target = OpenForEdit(row);
        using var zip = target.Zip;
        return target.Game is { } game && RomStorage.IsPending(handler, game) ? new(StorageMigrationPending, StorageMigrationPendingTitle) : null;
    }

    /// <summary>The game's ZIP; an external game's through a checked handle that must be the mirrored game (G4, I4).</summary>
    private LibraryTarget OpenForEdit(GameRow row) =>
        LibraryTarget.OpenForWrite(paths.Value.Roms, row.ZipPath, reader, systems, row.Id, row.SystemId);

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

    /// <summary>Atomic ZIP write (§6.1) then the DB mirror. Not cancellable once started, so a ZIP is never left half-applied.</summary>
    private async Task<LibraryEditResult> WriteAsync(LibraryZip existing, string zipPath, InfoJsonGame game,
        Dictionary<string, Func<Stream>>? newEntries = null, IReadOnlySet<string>? removedEntries = null, SavesChange? saves = null) =>
        await WriteZipAsync(existing, zipPath, game, newEntries ?? [], removedEntries, saves).ConfigureAwait(false)
        ?? await MirrorAsync(() => mirror.ReplaceAsync(Mirror(zipPath, game), CancellationToken.None), zipPath).ConfigureAwait(false);

    /// <param name="existing">The game's current ZIP (disposed by the writer), or null for a new game.</param>
    /// <returns>null when written and verified.</returns>
    private async Task<LibraryEditResult?> WriteZipAsync(LibraryZip? existing, string zipPath, InfoJsonGame game,
        Dictionary<string, Func<Stream>> newEntries, IReadOnlySet<string>? removedEntries = null, SavesChange? saves = null)
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
        if (before.IgdbId != after.IgdbId) changed.Add("igdbId");
        return changed;
    }

    private InfoJsonGameMetadata Touched(InfoJsonGame current) =>
        current.Metadata with { UpdatedAt = time.GetUtcNow(), AppVersion = AppInfo.Version };
}
