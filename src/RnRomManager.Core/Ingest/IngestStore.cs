using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>Stores an identified release: a new game, an added release, a duplicate or a repair, for <see cref="IngestPipeline"/>.</summary>
internal sealed class IngestStore(
    IngestPipeline pipeline,
    ISystemRegistry systems,
    IRomRepository roms,
    IGameRepository games,
    IIntegrityIssueRepository issues,
    InfoJsonReader reader,
    MediaService media,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger<IngestPipeline> logger)
{
    /// <summary>Shared steps 6–11 for synchronous review resolution; Attach supplies a target and skips grouping.</summary>
    /// <param name="reviewAction">The resolution action (<c>Dat</c>, <c>Attach</c>, <c>Manual</c>), for the audit row's details.</param>
    /// <param name="bytes">The ROM of an embedded handler; null for an external one, whose file is re-hashed streamed and moved (T9).</param>
    /// <param name="snapshot">The external file as it was checked; required when <paramref name="bytes"/> is null.</param>
    /// <param name="progress">B3: an external file's <c>Hashing</c> and <c>CopyingToLibrary</c> steps.</param>
    internal async Task<IngestItemResult> StoreReleaseAsync(
        IngestFile file,
        ISystemHandler system,
        InfoJsonRom rom,
        DatMetadata metadata,
        byte[]? bytes,
        Guid? targetGameId,
        Func<Guid, Task> afterMirror,
        AuditActor actor,
        string? reviewAction,
        CancellationToken cancellationToken,
        ExternalFileSnapshot? snapshot = null,
        JobProgress? progress = null)
    {
        ExternalSource? source = null;
        if (bytes is null)
        {
            var root = file.ArchiveEntry is null ? paths.Value.Ingest : paths.Value.Cache;
            var (outcome, hashed, details) = await pipeline.HashCheckedAsync(file, root, file.FullPath, snapshot!, system.MaxRomSizeBytes, recheckPath: true,
                cancellationToken, progress).ConfigureAwait(false);
            if (outcome == HashOutcome.Failed)
                return IngestItemResult.Error(IngestErrors.IoError, details, system.Id, rom.Metadata.Sha1);
            if (outcome == HashOutcome.Changed || hashed!.Sha1 != rom.Metadata.Sha1)
            {
                return IngestItemResult.Error(IngestErrors.VerifyFailed, details ?? $"{file.FullPath}: sha1 {hashed!.Sha1}, not {rom.Metadata.Sha1}",
                    system.Id, rom.Metadata.Sha1);
            }
            rom = rom with { Metadata = rom.Metadata with { SizeBytes = hashed.SizeBytes } };
            source = new ExternalSource(root, file.FullPath, snapshot!, progress);
        }

        var owners = targetGameId is { } target ? new[] { target } :
            await roms.ListGameIdsByBaseTitleAsync(system.Id, rom.BaseTitle, cancellationToken).ConfigureAwait(false);
        if (owners.Count > 1)
            return IngestItemResult.Error("AmbiguousGroup", "Several games own pipeline base title; use Attach.");
        var result = new IngestItemResult(IngestOutcome.Ingested, SystemId: system.Id, Sha1: rom.Metadata.Sha1);
        var stored = owners.Count == 0
            ? await NewGameAsync(file, system, rom, metadata, bytes, new CircuitBreaker(), result, time.GetUtcNow(), actor, reviewAction, cancellationToken, afterMirror, source).ConfigureAwait(false)
            : await AddToGameAsync(file, system, owners[0], rom, metadata, bytes, new CircuitBreaker(), result, time.GetUtcNow(), actor, reviewAction, cancellationToken, afterMirror, source).ConfigureAwait(false);
        // T9: a source that changed before it could be moved is a verification failure for Review, not a skip.
        return stored.Outcome == IngestOutcome.Skipped
            ? IngestItemResult.Error(IngestErrors.VerifyFailed, $"{file.FullPath} changed before it could be moved", system.Id, rom.Metadata.Sha1)
            : stored;
    }

    /// <param name="bytes">The ROM for the ZIP; null when <paramref name="source"/> is an external ROM's file.</param>
    /// <param name="set">B7: every file of a bin/cue set in placement order (cue first); null for a single file.</param>
    internal async Task<IngestItemResult> NewGameAsync(
        IngestFile file,
        ISystemHandler system,
        InfoJsonRom rom,
        DatMetadata metadata,
        byte[]? bytes,
        CircuitBreaker breaker,
        IngestItemResult result,
        DateTimeOffset now,
        AuditActor actor,
        string? reviewAction,
        CancellationToken cancellationToken,
        Func<Guid, Task>? afterMirror = null,
        ExternalSource? source = null,
        IReadOnlyList<SetPlacement>? set = null)
    {
        // Step 7: a new game takes every metadata field. Step 8: media.
        var found = await media.FindAsync(system, [rom.Identification.DatGameName], breaker, cancellationToken).ConfigureAwait(false);
        var title = rom.BaseTitle;
        var game = new InfoJsonGame
        {
            SchemaVersion = InfoJsonReader.CurrentVersion,
            GameId = Guid.NewGuid(),
            System = system.Id,
            Title = title,
            DisplayTitle = DisplayTitle.From(title),
            ReleaseYear = metadata.ReleaseYear,
            Developers = metadata.Developers,
            Publishers = metadata.Publishers,
            Genres = metadata.Genres,
            Description = null,
            Rating = null,
            Favourite = false,
            Roms = PrimaryRelease.Apply([rom], system.DatSource),
            Images = GameImages.ForNewGame(found, now),
            Metadata = new InfoJsonGameMetadata(now, now, AppInfo.Version),
        };

        if (source is not null)
        {
            // E2: the ZIP and its ROM file get budgeted names, free on disk and against every mirrored claim.
            var library = paths.Value.Roms;
            var bucket = ExternalRomPath.EnsureDirectory(library, Path.Combine(library, system.Id, Bucket.For(ExternalRomName.ZipName(title))));
            if (bucket.Status != ExternalPathStatus.Found)
                return IngestItemResult.Error(IngestErrors.IoError, bucket.Details, system.Id, result.Sha1);

            var claims = await roms.ListExternalAsync(cancellationToken).ConfigureAwait(false);
            var externalZipPath = await ExternalRomName.AllocateZipPathAsync(library, system.Id, title, claims, games, cancellationToken).ConfigureAwait(false);
            var folderPath = externalZipPath[..externalZipPath.LastIndexOf('/')];
            var reservations = ExternalNameReservations.ForFolder(bucket.Path, folderPath, claims);
            string name;
            if (system.FileSets)
            {
                if (ExactNamesError(rom, reservations, system.Id, result.Sha1) is { } refused)
                    return refused;
                name = rom.FileName;
            }
            else
            {
                name = ExternalRomName.Allocate(Path.GetFileNameWithoutExtension(externalZipPath), rom.FileName, reservations);
            }
            game = game with { Roms = PrimaryRelease.Apply([rom with { ExternalFileName = name }], system.DatSource) };
            var images = new Dictionary<string, Func<Stream>>();
            IngestPackager.AddImages(images, found.BoxArt, found.Snap);
            return await pipeline.IngestPackager.PackageAsync(file, null, externalZipPath, game, images, result, actor, reviewAction, rom.FileName, cancellationToken,
                afterMirror, placement: new ExternalPlacement(source, name), set: set).ConfigureAwait(false);
        }

        // §6.1: Sanitize(title).zip in roms/<system>/<bucket>/; a clash (any case) with another game gets " (2)"….
        var zipName = FileNameSanitizer.Sanitize(title) + ".zip";
        var folder = Path.Combine(paths.Value.Roms, system.Id, Bucket.For(zipName));
        Directory.CreateDirectory(folder);
        zipName = FileNameRules.FirstFreeIn(folder, zipName);
        var zipPath = $"{system.Id}/{Bucket.For(zipName)}/{zipName}";

        var entries = new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(bytes!) };
        IngestPackager.AddImages(entries, found.BoxArt, found.Snap);
        return await pipeline.IngestPackager.PackageAsync(file, null, zipPath, game, entries, result, actor, reviewAction, rom.FileName, cancellationToken, afterMirror).ConfigureAwait(false);
    }

    /// <param name="bytes">The ROM for the ZIP; null when <paramref name="source"/> is an external ROM's file.</param>
    /// <param name="set">B7: every file of a bin/cue set in placement order (cue first); null for a single file.</param>
    internal async Task<IngestItemResult> AddToGameAsync(
        IngestFile file,
        ISystemHandler system,
        Guid gameId,
        InfoJsonRom rom,
        DatMetadata metadata,
        byte[]? bytes,
        CircuitBreaker breaker,
        IngestItemResult result,
        DateTimeOffset now,
        AuditActor actor,
        string? reviewAction,
        CancellationToken cancellationToken,
        Func<Guid, Task>? afterMirror = null,
        ExternalSource? source = null,
        IReadOnlyList<SetPlacement>? set = null)
    {
        var row = await games.GetAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (row is null)
            return IngestItemResult.Error(IngestErrors.TargetZipUnreadable, $"game {gameId} has ROM rows but no games row", system.Id, result.Sha1);

        // I4: an external game's ZIP is opened through a checked handle and must be the game its mirror row names.
        var (existing, opened, error) = source is null
            ? OpenTarget(row.ZipPath, system.Id, result.Sha1)
            : Target(LibraryTarget.OpenChecked(paths.Value.Roms, row.ZipPath, reader, gameId, system.Id), system.Id, result.Sha1);
        if (error is not null)
            return error;
        var current = opened!;

        using (existing)
        {
            // S3: before any other conflict check; a legacy game is read-only until the storage migration.
            if (RomStorage.IsPending(system, current))
                return IngestItemResult.Error(IngestErrors.StorageMigrationPending, $"{row.ZipPath} still holds its ROMs; waiting for the storage migration",
                    system.Id, result.Sha1);
            ExternalPlacement? placement = null;
            if (source is null)
            {
                // §6.1 entry name clash: " (2)"… before the extension.
                rom = rom with { FileName = FileNameRules.FirstFree(rom.FileName, current.Roms.Select(r => r.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase).Contains) };
            }
            else
            {
                // E3 logical name against the game's releases; E2 file name against the folder, every claim on it and pipeline ZIP's own pointers.
                // B10: a FileSets name is never suffixed; a clash with the game's own releases is NameTaken.
                var logicalNames = new ExternalNameReservations(current.Roms.Select(r => r.FileName));
                if (system.FileSets && logicalNames.IsTaken(rom.FileName))
                    return IngestItemResult.Error(IngestErrors.NameTaken, $"'{rom.FileName}' is already a release of pipeline game", system.Id, result.Sha1);
                if (!system.FileSets)
                    rom = rom with { FileName = ExternalRomName.AllocateLogicalName(rom.FileName, logicalNames) };
                var library = paths.Value.Roms;
                var folder = ExternalRomPath.ClassifyDirectory(library, Path.GetDirectoryName(SafePath.Resolve(library, row.ZipPath))!);
                if (folder.Status != ExternalPathStatus.Found)
                    return IngestItemResult.Error(IngestErrors.IoError, folder.Details, system.Id, result.Sha1);
                var claims = await roms.ListExternalAsync(cancellationToken).ConfigureAwait(false);
                var reservations = ExternalNameReservations.ForFolder(folder.Path, row.ZipPath[..row.ZipPath.LastIndexOf('/')], claims,
                    current.ExternalFileNames());
                if (system.FileSets && ExactNamesError(rom, reservations, system.Id, result.Sha1) is { } refused)
                    return refused;
                var name = system.FileSets ? rom.FileName : ExternalRomName.Allocate(Path.GetFileNameWithoutExtension(row.ZipPath), rom.FileName, reservations);
                rom = rom with { ExternalFileName = name };
                placement = new ExternalPlacement(source, name);
            }
            var allRoms = PrimaryRelease.Apply([.. current.Roms, rom], system.DatSource, current.ChosenPrimarySha1);

            // Step 7: only fields that are still empty.
            var game = current with
            {
                ReleaseYear = current.ReleaseYear ?? metadata.ReleaseYear,
                Developers = current.Developers.Count > 0 ? current.Developers : metadata.Developers,
                Publishers = current.Publishers.Count > 0 ? current.Publishers : metadata.Publishers,
                Genres = current.Genres.Count > 0 ? current.Genres : metadata.Genres,
                Roms = allRoms,
                Metadata = current.Metadata with { UpdatedAt = now, AppVersion = AppInfo.Version },
            };

            var entries = new Dictionary<string, Func<Stream>>();
            if (source is null)
                entries[LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(bytes!);

            // Step 8 (I4): only while the game still has the placeholder, or no images at all.
            if (current.Images.Count == 0 || current.Images.Any(i => i.IsDefault))
            {
                var names = PrimaryRelease.Order(allRoms, system.DatSource).Select(r => r.Identification.DatGameName);
                var found = await media.FindAsync(system, names, breaker, cancellationToken).ConfigureAwait(false);
                if (GameImages.ApplyFetched(current.Images, found, now) is var (images, imageEntries))
                {
                    game = game with { Images = images };
                    foreach (var (name, content) in imageEntries)
                        entries[LibraryZip.ImageEntry(name)] = () => new MemoryStream(content);
                }
            }

            // B9: the new release never adopts an orphan note of its name.
            return await pipeline.IngestPackager.PackageAsync(file, existing, row.ZipPath, game, entries, result with { GameId = gameId }, actor, reviewAction, rom.FileName, cancellationToken,
                afterMirror, new HashSet<string>(StringComparer.Ordinal) { LibraryZip.NoteEntry(rom.FileName) }, placement, set: set).ConfigureAwait(false);
        }
    }

    /// <summary>B10: reserves the exact names of a release's main file and its set files, or the first error; nothing has moved yet.</summary>
    internal static IngestItemResult? ExactNamesError(
        InfoJsonRom rom,
        ExternalNameReservations reservations,
        string systemId,
        string? sha1) =>
        new[] { rom.FileName }.Concat(rom.Files?.Select(f => f.ExternalFileName) ?? [])
            .Select(name => ExactNameError(name, reservations, systemId, sha1)).FirstOrDefault(e => e is not null);

    /// <summary>B10: reserves a <c>FileSets</c> file's exact name, or the <c>NameTaken</c>/<c>NameTooLong</c> error; nothing has moved yet.</summary>
    internal static IngestItemResult? ExactNameError(
        string name,
        ExternalNameReservations reservations,
        string systemId,
        string? sha1) =>
        ExternalRomName.AllocateExact(name, reservations) switch
        {
            ExactNameStatus.Taken => IngestItemResult.Error(IngestErrors.NameTaken, $"'{name}' is already used in the game's folder", systemId, sha1),
            ExactNameStatus.TooLong => IngestItemResult.Error(IngestErrors.NameTooLong,
                $"'{name}' is longer than {ExternalRomName.MaxNameBytes} UTF-8 bytes", systemId, sha1),
            _ => null,
        };

    /// <summary>§6.1: every write starts from the ZIP's own <c>info.json</c>.</summary>
    internal (LibraryZip? Zip, InfoJsonGame? Game, IngestItemResult? Error) OpenTarget(string zipPath, string systemId, string? sha1)
    {
        return Target(LibraryTarget.Open(paths.Value.Roms, zipPath, reader), systemId, sha1);
    }

    /// <summary>An external game's ZIP opened for checking (spec W1): it must be the game and system the mirror names.</summary>
    internal LibraryTarget OpenTargetChecked(string zipPath, Guid gameId, string systemId) =>
        LibraryTarget.OpenChecked(paths.Value.Roms, zipPath, reader, gameId, systemId);

    internal static (LibraryZip? Zip, InfoJsonGame? Game, IngestItemResult? Error) Target(LibraryTarget target, string systemId, string? sha1) =>
        target.Code is null
            ? (target.Zip, target.Game, null)
            : (null, null, IngestItemResult.Error(target.Code, target.Details, systemId, sha1));

    /// <summary>
    /// Step 3: the SHA1 is already in the library. Its entry has an open <c>HashMismatch</c>/<c>EntryMissing</c>/
    /// <c>EntrySizeMismatch</c> issue → rewrite it from pipeline file and resolve the issue (<c>Repaired</c>, D30);
    /// otherwise <c>Duplicate</c> → <c>_duplicates/</c>.
    /// </summary>
    /// <param name="bytes">The incoming embedded ROM; null for an external input.</param>
    /// <param name="source">The incoming external ROM's checked file; null for an embedded input.</param>
    internal async Task<IngestItemResult> DuplicateOrRepairAsync(
        IngestFile file,
        RomRow known,
        byte[]? bytes,
        ExternalSource? source,
        long size,
        string sha1,
        AuditActor actor,
        CancellationToken cancellationToken)
    {
        var game = await games.GetAsync(known.GameId, cancellationToken).ConfigureAwait(false);
        var entryName = known.ExternalFileName ?? LibraryZip.RomEntry(known.FileName);
        IReadOnlyList<OpenIssue> open = [];
        // I6: only an input of the owner's storage mode repairs it; across modes the bytes on disk may differ.
        // S3a, B5: the owner row's own layout, so a game in the other layout than its system's setting never takes a cross-mode repair.
        var ownerExternal = systems.ById(known.SystemId) is { } ownerSystem && RomStorage.MayHoldExternalFiles(ownerSystem) && known.ExternalFileName is not null;
        if (game is not null && ownerExternal == (source is not null))
        {
            if (source is null)
            {
                open = await issues.ListOpenAsync(game.ZipPath, entryName, IntegrityIssueType.SelfRepairable, cancellationToken).ConfigureAwait(false);
            }
            else if (known.ExternalFileName is { } pointer)
            {
                // The table's collation matches accent-distinct names too: only pipeline exact file of pipeline game counts.
                open = [.. (await issues.ListOpenExternalAsync(game.ZipPath, pointer, IntegrityIssueType.SelfRepairable, cancellationToken).ConfigureAwait(false))
                    .Where(i => string.Equals(i.ZipPath, game.ZipPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(i.EntryName, pointer, StringComparison.OrdinalIgnoreCase)
                        && (i.GameId is null || i.GameId == known.GameId))
                    .Select(i => new OpenIssue(i.Id, i.Type))];
            }
        }

        if (open.Count == 0)
        {
            logger.LogWarning("{Path}: duplicate of {Zip} › {Entry} (sha1 {Sha1}, size {Size})",
                file.FullPath, game?.ZipPath, entryName, sha1, size);
            // An archive entry isn't moved on its own: the archive's fate covers it (§7.3).
            var moveError = file.ArchiveEntry is null ? pipeline.IngestFileMoves.MoveToDuplicates(file, actor, source?.Progress) : null;
            return moveError ?? new IngestItemResult(IngestOutcome.Duplicate, Details: $"same as {game?.ZipPath} › {entryName}",
                SystemId: known.SystemId, GameId: known.GameId, Sha1: sha1);
        }
        if (source is not null)
            return await RepairExternalAsync(file, known, game!, open, source, sha1, actor, cancellationToken).ConfigureAwait(false);

        var (existing, current, error) = OpenTarget(game!.ZipPath, known.SystemId, sha1);
        if (error is not null)
            return error;
        using (existing)
        {
            var now = time.GetUtcNow();
            var repaired = current! with
            {
                Roms = current.Roms.Select(r => r.Metadata.Sha1 == sha1 ? r with { Metadata = r.Metadata with { DateModified = now } } : r).ToList(),
                Metadata = current.Metadata with { UpdatedAt = now, AppVersion = AppInfo.Version },
            };
            var entries = new Dictionary<string, Func<Stream>> { [entryName] = () => new MemoryStream(bytes!) };
            var result = new IngestItemResult(IngestOutcome.Repaired, Details: $"rewrote {game.ZipPath} › {entryName} ({string.Join(", ", open.Select(i => i.Type))})",
                SystemId: known.SystemId, GameId: known.GameId, Sha1: sha1);
            return await pipeline.IngestPackager.PackageAsync(file, existing, game.ZipPath, repaired, entries, result, actor, null, known.FileName, cancellationToken,
                _ => issues.ResolveAsync(open.Select(i => i.Id).ToList(), now.UtcDateTime, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// I6 for an external ROM: the incoming file is moved over the release's <c>externalFileName</c> and the ZIP
    /// rewritten. A file still at that name is set aside under a reserved <c>.repair.tmp</c> name first and only
    /// removed once the ZIP and database updates succeed. A failed ZIP write attempts to put both files back.
    /// </summary>
    /// <param name="setFile">B14: the <c>files</c> entry of release <paramref name="known"/> to repair; null for its main file.</param>
    internal async Task<IngestItemResult> RepairExternalAsync(
        IngestFile file,
        RomRow known,
        GameRow game,
        IReadOnlyList<OpenIssue> open,
        ExternalSource source,
        string sha1,
        AuditActor actor,
        CancellationToken cancellationToken,
        string? setFile = null)
    {
        var library = paths.Value.Roms;
        var (existing, current, error) = Target(LibraryTarget.OpenChecked(library, game.ZipPath, reader, known.GameId, known.SystemId), known.SystemId, sha1);
        if (error is not null)
            return error;
        using (existing)
        {
            IngestItemResult Fail(string code, string? details) => IngestItemResult.Error(code, details, known.SystemId, sha1);
            var rom = current!.Roms.FirstOrDefault(r => r.Metadata.Sha1 == known.Sha1);
            var expected = setFile ?? known.ExternalFileName;
            var pointer = setFile is null ? rom?.ExternalFileName
                : rom?.Files?.FirstOrDefault(f => f.ExternalFileName == setFile && f.Sha1 == sha1)?.ExternalFileName;
            if (rom is null || pointer is null || !string.Equals(pointer, expected, StringComparison.Ordinal))
            {
                return Fail(IngestErrors.TargetZipUnreadable, $"{game.ZipPath}: the mirror says {expected} holds sha1 {sha1}, " +
                    $"but info.json says {pointer ?? "no release has it"}; the mirror is stale");
            }

            var folder = ExternalRomPath.ClassifyDirectory(library, Path.GetDirectoryName(SafePath.Resolve(library, game.ZipPath))!);
            if (folder.Status != ExternalPathStatus.Found)
                return Fail(IngestErrors.IoError, folder.Details);
            // Found in any letter case, or missing; several spellings or anything unsafe is never repaired.
            var there = ExternalRomPath.ClassifyFile(library, Path.Combine(folder.Path, pointer));
            if (there.Status is not (ExternalPathStatus.Found or ExternalPathStatus.Missing))
                return Fail(IngestErrors.IoError, $"{pointer} can't be repaired: {there.Details}");

            var claims = await roms.ListExternalAsync(cancellationToken).ConfigureAwait(false);
            var names = ExternalNameReservations.ForFolder(folder.Path, game.ZipPath[..game.ZipPath.LastIndexOf('/')], claims,
                current.ExternalFileNames());
            var plan = new RepairPlan(
                there.Status == ExternalPathStatus.Found ? there.Path : null, there.Snapshot,
                Path.Combine(folder.Path, ExternalRomName.AllocateRepairBackup(pointer, names)),
                Path.Combine(folder.Path, ExternalRomName.Allocate(Path.GetFileNameWithoutExtension(game.ZipPath), setFile is null ? rom.FileName : pointer, names)));

            var now = time.GetUtcNow();
            var repaired = current with
            {
                // A set file has no date of its own; a main file's dateModified records the rewrite.
                Roms = current.Roms.Select(r => setFile is null && r.Metadata.Sha1 == sha1 ? r with { Metadata = r.Metadata with { DateModified = now } } : r).ToList(),
                Metadata = current.Metadata with { UpdatedAt = now, AppVersion = AppInfo.Version },
            };
            var result = new IngestItemResult(IngestOutcome.Repaired, Details: $"rewrote {game.ZipPath} › {pointer} ({string.Join(", ", open.Select(i => i.Type))})",
                SystemId: known.SystemId, GameId: known.GameId, Sha1: sha1);
            return await pipeline.IngestPackager.PackageAsync(file, existing, game.ZipPath, repaired, [], result, actor, null, rom.FileName, cancellationToken,
                _ => issues.ResolveAsync(open.Select(i => i.Id).ToList(), now.UtcDateTime, CancellationToken.None),
                placement: new ExternalPlacement(source, pointer), repair: plan).ConfigureAwait(false);
        }
    }
}
