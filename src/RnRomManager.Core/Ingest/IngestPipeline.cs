using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>What happened to one ingest item: becomes its <c>job_items</c> row.</summary>
public sealed record IngestItemResult(
    string Outcome, string? Reason = null, string? Details = null, string? SystemId = null, Guid? GameId = null, string? Sha1 = null)
{
    /// <summary>Only affects archive fate: a previously resolved entry counts as ingested (§7.3).</summary>
    public bool ResolvedArchiveDuplicate { get; init; }

    public static IngestItemResult Error(string reason, string? details, string? systemId = null, string? sha1 = null) =>
        new(IngestOutcome.Error, reason, details, systemId, Sha1: sha1);
}

/// <summary>§7.2 reason codes for <c>Error</c> items (spec §23.1).</summary>
public static class IngestErrors
{
    public const string IoError = "IoError";
    public const string VerifyFailed = "VerifyFailed";
    public const string DbWriteFailed = "DbWriteFailed";
    public const string TargetZipUnreadable = "TargetZipUnreadable";
    public const string InfoJsonNewerSchema = "InfoJsonNewerSchema";
    public const string Unexpected = "Unexpected";
}

/// <summary>§7.2 reason codes for <c>Failed</c> items (spec §23.1): the file moves to <c>_failed/</c>.</summary>
public static class IngestFailures
{
    public const string UnsupportedType = "UnsupportedType";
    public const string TooSmall = "TooSmall";
    public const string TooLarge = "TooLarge";
    public const string InvalidHeader = "InvalidHeader";
}

/// <summary>
/// One loose file through spec §7.2: validate → hash → identify by DAT → group → metadata → media → package (atomic
/// ZIP) → mirror (DB) → delete the source. Every write starts from the game's ZIP, never from DB rows.
/// </summary>
public sealed class IngestPipeline(
    DatCache dats,
    ISystemRegistry systems,
    IRomRepository roms,
    IGameRepository games,
    ILibraryMirror mirror,
    IIntegrityIssueRepository issues,
    IIngestReviewRepository reviews,
    InfoJsonReader reader,
    MediaService media,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    AuditPaths auditPaths,
    TimeProvider time,
    ILogger<IngestPipeline> logger)
{
    /// <summary>Test seam: runs once an external source is open and checked, right before it is hashed.</summary>
    public Action<IngestFile>? BeforeExternalHash { get; init; }

    /// <summary>AU1: the source as recorded: <c>ingest/…</c>, or <c>ingest/&lt;archive&gt; › &lt;entry&gt;</c> for an archive entry.</summary>
    private string AuditSource(IngestFile file) =>
        file.ArchiveEntry is { } entry ? auditPaths.ArchiveEntry(file.ArchivePath!, entry) : auditPaths.Of(file.FullPath);

    /// <summary>AU4 <c>ArchiveDeleted</c>: called by <see cref="IngestJob"/> after it deleted a fully ingested archive.</summary>
    public void RecordArchiveDeleted(IngestFile archive, AuditActor actor) =>
        audit.Record(new AuditEvent(AuditActions.ArchiveDeleted, actor, archive.SystemId, Path: auditPaths.Of(archive.FullPath)));

    /// <summary>Processes one source and completes any pending review row for that source.</summary>
    public async Task<IngestItemResult?> ProcessAsync(IngestFile file, Guid jobId, AuditActor actor, CircuitBreaker breaker,
        CancellationToken cancellationToken)
    {
        var result = await ProcessCoreAsync(file, jobId, actor, breaker, cancellationToken).ConfigureAwait(false);
        // I4: an external source's Error keeps its Pending row (CompleteSourceAsync would delete it); stale cleanup handles absence.
        if (result is not null && !(IsExternal(file) && result.Outcome == IngestOutcome.Error))
            await reviews.CompleteSourceAsync(file.SourcePath, file.ArchiveEntry, result, jobId, time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<IngestItemResult?> ProcessCoreAsync(IngestFile file, Guid jobId, AuditActor actor, CircuitBreaker breaker,
        CancellationToken cancellationToken)
    {
        // Step 1: validate.
        var folder = systems.ById(file.SystemId) ?? throw new InvalidOperationException($"No handler for ingest folder '{file.SystemId}'.");
        var family = systems.ByFamily(folder.Family);
        var extension = Path.GetExtension(file.Name);
        var byExtension = family.FirstOrDefault(h => h.RomExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (byExtension is null)
            return FailFile(file, actor, IngestFailures.UnsupportedType, $"'{extension}' isn't a ROM extension of the {folder.Family} family");
        if (!folder.EmbedRomsInZip)
            return await ProcessExternalAsync(file, folder, family, byExtension, jobId, actor, breaker, cancellationToken).ConfigureAwait(false);
        if (file.SizeBytes < byExtension.MinRomSizeBytes)
            return FailFile(file, actor, IngestFailures.TooSmall, $"{file.SizeBytes} bytes; {byExtension.Id} ROMs are at least {byExtension.MinRomSizeBytes}");
        if (file.SizeBytes > byExtension.MaxRomSizeBytes)
            return FailFile(file, actor, IngestFailures.TooLarge, $"{file.SizeBytes} bytes; {byExtension.Id} ROMs are at most {byExtension.MaxRomSizeBytes}");

        // Step 2: hash the normalised bytes (what gets stored).
        byte[] bytes;
        try
        {
            bytes = byExtension.NormalizeForHashing(await File.ReadAllBytesAsync(file.FullPath, cancellationToken).ConfigureAwait(false)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return IngestItemResult.Error(IngestErrors.IoError, ex.Message, file.SystemId);
        }
        var hashes = RomHashes.Of(bytes);
        logger.LogDebug("{Path}: {Size} bytes, crc32 {Crc32}, sha1 {Sha1}", file.FullPath, bytes.Length, hashes.Crc32, hashes.Sha1);
        return await IdentifyAsync(file, folder, family, hashes, bytes.Length, bytes, bytes, jobId, actor, breaker, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// I1–I3 for an external system: the file is opened through a checked handle and hashed in one streamed read,
    /// never held in memory. A loose file that differs from the ingest listing, is under 10 s old or changes while
    /// it is hashed is <c>Skipped</c> <c>TooRecent</c> and left where it is.
    /// </summary>
    private async Task<IngestItemResult?> ProcessExternalAsync(IngestFile file, ISystemHandler folder, IReadOnlyList<ISystemHandler> family,
        ISystemHandler byExtension, Guid jobId, AuditActor actor, CircuitBreaker breaker, CancellationToken cancellationToken)
    {
        var tooRecent = new IngestItemResult(IngestOutcome.Skipped, IngestReason.TooRecent, SystemId: file.SystemId);
        var loose = file.ArchiveEntry is null;
        // An archive entry was extracted into the job's work folder under the cache root.
        var root = loose ? paths.Value.Ingest : paths.Value.Cache;
        var fresh = ExternalRomPath.ClassifyFile(root, file.FullPath);
        if (fresh.Status != ExternalPathStatus.Found)
            return IngestItemResult.Error(IngestErrors.IoError, fresh.Details, file.SystemId);
        var snapshot = fresh.Snapshot!;
        if (loose && (snapshot.Length != file.SizeBytes || snapshot.LastWriteTimeUtc != file.ModifiedAtUtc
            || time.GetUtcNow().UtcDateTime - snapshot.LastWriteTimeUtc < IngestJob.MinFileAge))
            return tooRecent;

        var size = snapshot.Length;
        if (size < byExtension.MinRomSizeBytes)
            return FailFile(file, actor, IngestFailures.TooSmall, $"{size} bytes; {byExtension.Id} ROMs are at least {byExtension.MinRomSizeBytes}");
        if (size > byExtension.MaxRomSizeBytes)
            return FailFile(file, actor, IngestFailures.TooLarge, $"{size} bytes; {byExtension.Id} ROMs are at most {byExtension.MaxRomSizeBytes}");

        var opened = ExternalRomPath.OpenRead(root, fresh.Path, FileShare.ReadWrite | FileShare.Delete, snapshot);
        if (opened.Status == ExternalPathStatus.Changed)
            return tooRecent;
        if (opened.Status != ExternalPathStatus.Found)
            return IngestItemResult.Error(IngestErrors.IoError, opened.Details, file.SystemId);
        RomFileHashes? hashed;
        using (var stream = opened.Stream!)
        {
            BeforeExternalHash?.Invoke(file);
            try
            {
                hashed = await RomFileHasher.HashAsync(stream, byExtension.MaxRomSizeBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return IngestItemResult.Error(IngestErrors.IoError, ex.Message, file.SystemId);
            }
            // Grew past the cap, or changed on its handle or at its path while it was read: still being copied in.
            if (hashed is null || hashed.SizeBytes != size || ExternalRomPath.SnapshotOf(stream) != snapshot
                || (loose && ExternalRomPath.ClassifyFile(root, fresh.Path).Snapshot != snapshot))
                return tooRecent;
        }
        logger.LogDebug("{Path}: {Size} bytes streamed, crc32 {Crc32}, sha1 {Sha1}", file.FullPath, size, hashed.Crc32, hashed.Sha1);

        // I3: only handlers that can store this extension and size identify it.
        var extension = Path.GetExtension(file.Name);
        var eligible = family.Where(h => h.RomExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            && size >= h.MinRomSizeBytes && size <= h.MaxRomSizeBytes).ToList();
        return await IdentifyAsync(file, folder, eligible, new RomHashes(hashed.Crc32, hashed.Md5, hashed.Sha1), size, hashed.Header, null,
            jobId, actor, breaker, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Steps 3–6 on hashed input.</summary>
    /// <param name="candidates">The handlers that may claim it: the whole family, or the eligible members for an external system.</param>
    /// <param name="headerBytes">What <c>ReadHeader</c> gets: the whole ROM, or its first MiB for an external one.</param>
    /// <param name="bytes">The ROM to store; null for an external ROM, which is never held in memory.</param>
    private async Task<IngestItemResult?> IdentifyAsync(IngestFile file, ISystemHandler folder, IReadOnlyList<ISystemHandler> candidates,
        RomHashes hashes, long size, byte[] headerBytes, byte[]? bytes, Guid jobId, AuditActor actor, CircuitBreaker breaker,
        CancellationToken cancellationToken)
    {
        var external = bytes is null;
        // Step 3: global dedupe, self-repair, and review-item lifecycle.
        var review = await reviews.GetBySha1Async(hashes.Sha1, cancellationToken).ConfigureAwait(false);
        if (await roms.GetBySha1Async(hashes.Sha1, cancellationToken).ConfigureAwait(false) is { } known)
        {
            var duplicate = await DuplicateOrRepairAsync(file, known, bytes, size, hashes.Sha1, actor, cancellationToken).ConfigureAwait(false);
            return duplicate with { ResolvedArchiveDuplicate = duplicate.Outcome == IngestOutcome.Duplicate &&
                file.ArchivePath is not null && review is { Status: "Resolved" } && review.ArchivePath == file.ArchivePath };
        }
        if (review is { Status: "Pending" } && review.SourcePath != file.SourcePath)
        {
            logger.LogWarning("{Path}: duplicate of pending review source {ReviewPath} (crc32 {Crc32}, md5 {Md5}, sha1 {Sha1})",
                file.SourcePath, review.SourcePath, hashes.Crc32, hashes.Md5, hashes.Sha1);
            return (file.ArchiveEntry is null ? MoveToDuplicates(file, actor) : null) ??
                new IngestItemResult(IngestOutcome.Duplicate, Details: $"same as pending review source {review.SourcePath}", SystemId: review.SystemId, Sha1: hashes.Sha1);
        }
        if (review is { Status: "Dismissed" } && file.ArchivePath is not null && review.ArchivePath == file.ArchivePath)
            return FailFile(file, actor, "Dismissed", "this entry was dismissed from this archive", hashes.Sha1);
        if (review is { Status: "Dismissed" } && file.ArchivePath is null)
            await reviews.UpsertAsync(review with
            {
                SourcePath = file.SourcePath, ArchivePath = null, ArchiveEntry = null, OriginalFileName = file.Name,
                LastSeenJobId = jobId,
            }, cancellationToken).ConfigureAwait(false);

        // Step 4: header from every handler of the family; the DAT match decides the system (D23).
        var headers = candidates.ToDictionary(h => h.Id, h => h.ReadHeader(headerBytes));
        var match = candidates
            .Select(h => (Handler: h, Store: dats.For(h.Id)))
            .Select(x => (x.Handler, x.Store, Rom: x.Store?.BySha1(hashes.Sha1)))
            .FirstOrDefault(x => x.Rom is not null);
        if (match.Rom is null)
        {
            // Step 5. Every ReadHeader null (headerless system) counts as plausible.
            var found = headers.Values.FirstOrDefault(h => h is not null);
            if (found is { IsPlausible: false })
                return FailFile(file, actor, IngestFailures.InvalidHeader, "no DAT match, and the header isn't valid for this system", hashes.Sha1);
            // I3: an external header naming an ineligible handler falls back to the folder's handler, or the first eligible one.
            var classified = candidates.FirstOrDefault(h => h.Id == found?.ClassifiedSystemId)
                ?? (external ? candidates.FirstOrDefault(h => h.Id == folder.Id) ?? candidates[0] : folder);
            var originalName = file.Name;
            if (file.ArchiveEntry is null && !external)
                file = RenameToClassifiedExtension(file, found, candidates, actor);
            return await QueueReviewAsync(file, originalName, classified.Id, "NoDatMatch", hashes, size, found, jobId,
                SuggestionEngine.Find(dats.For(classified.Id), found?.Title, originalName, size), null, null, cancellationToken).ConfigureAwait(false);
        }
        var (system, store, datRom) = (match.Handler, match.Store!, match.Rom);
        if (system.Id != file.SystemId)
            logger.LogInformation("{Path}: dropped in ingest/{Folder} but it's a {System} ROM; stored under roms/{System}", file.FullPath, file.SystemId, system.Id, system.Id);

        // I3: an external release keeps its source's extension (lower-cased) on the DAT stem, as a portable logical name.
        var romName = external
            ? ExternalRomName.AllocateLogicalName(Path.GetFileNameWithoutExtension(datRom.RomName) + Path.GetExtension(file.Name).ToLowerInvariant(),
                new ExternalNameReservations([]))
            : datRom.RomName;

        // Step 6: group by system + base title.
        var baseTitle = NoIntroName.Parse(datRom.GameName).BaseTitle;
        var owners = await roms.ListGameIdsByBaseTitleAsync(system.Id, baseTitle, cancellationToken).ConfigureAwait(false);
        if (owners.Count > 1)
        {
            return await QueueReviewAsync(file, file.Name, system.Id, "AmbiguousGroup", hashes, size, headers[system.Id], jobId,
                [], new { datName = store.DatName, datVersion = store.Version, datGameName = datRom.GameName, romName },
                owners, cancellationToken).ConfigureAwait(false);
        }

        if (bytes is null)
        {
            // shortcut: external placement (I4) lands in step 9f; until then nothing is written and the source stays.
            return IngestItemResult.Error(IngestErrors.IoError, "storing external ROMs is not available yet", system.Id, hashes.Sha1);
        }

        var now = time.GetUtcNow();
        var tags = NoIntroName.Parse(Path.GetFileNameWithoutExtension(datRom.RomName));
        var rom = new InfoJsonRom
        {
            FileName = romName,
            OriginalFileName = file.Name,
            BaseTitle = baseTitle,
            Regions = tags.Regions,
            Languages = tags.Languages,
            Revision = tags.Revision,
            Flags = tags.Flags,
            IsPrimary = false,
            Identification = new InfoJsonIdentification(IdentificationMethod.Dat, store.DatName, store.Version, datRom.GameName),
            Header = headers[system.Id] is { } header ? JsonSerializer.SerializeToNode(header.Fields) as JsonObject : null,
            Metadata = new InfoJsonRomMetadata(size, hashes.Crc32, hashes.Md5, hashes.Sha1, now, now, file.SourcePath),
        };
        var metadata = store.MetadataFor(datRom.Crc32);
        var result = new IngestItemResult(IngestOutcome.Ingested, SystemId: system.Id, Sha1: hashes.Sha1);

        return owners.Count == 0
            ? await NewGameAsync(file, system, rom, metadata, bytes, breaker, result, now, actor, null, cancellationToken).ConfigureAwait(false)
            : await AddToGameAsync(file, system, owners[0], rom, metadata, bytes, breaker, result, now, actor, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Shared steps 6–11 for synchronous review resolution; Attach supplies a target and skips grouping.</summary>
    /// <param name="reviewAction">The resolution action (<c>Dat</c>, <c>Attach</c>, <c>Manual</c>), for the audit row's details.</param>
    public async Task<IngestItemResult> StoreReleaseAsync(IngestFile file, ISystemHandler system, InfoJsonRom rom,
        DatMetadata metadata, byte[] bytes, Guid? targetGameId, Func<Guid, Task> afterMirror, AuditActor actor, string? reviewAction,
        CancellationToken cancellationToken)
    {
        var owners = targetGameId is { } target ? new[] { target } :
            await roms.ListGameIdsByBaseTitleAsync(system.Id, rom.BaseTitle, cancellationToken).ConfigureAwait(false);
        if (owners.Count > 1)
            return IngestItemResult.Error("AmbiguousGroup", "Several games own this base title; use Attach.");
        var result = new IngestItemResult(IngestOutcome.Ingested, SystemId: system.Id, Sha1: rom.Metadata.Sha1);
        return owners.Count == 0
            ? await NewGameAsync(file, system, rom, metadata, bytes, new CircuitBreaker(), result, time.GetUtcNow(), actor, reviewAction, cancellationToken, afterMirror).ConfigureAwait(false)
            : await AddToGameAsync(file, system, owners[0], rom, metadata, bytes, new CircuitBreaker(), result, time.GetUtcNow(), actor, reviewAction, cancellationToken, afterMirror).ConfigureAwait(false);
    }

    private async Task<IngestItemResult> NewGameAsync(
        IngestFile file, ISystemHandler system, InfoJsonRom rom, DatMetadata metadata, byte[] bytes, CircuitBreaker breaker,
        IngestItemResult result, DateTimeOffset now, AuditActor actor, string? reviewAction, CancellationToken cancellationToken,
        Func<Guid, Task>? afterMirror = null)
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
            Roms = PrimaryRelease.Apply([rom]),
            Images = GameImages.ForNewGame(found, now),
            Metadata = new InfoJsonGameMetadata(now, now, AppInfo.Version),
        };

        // §6.1: Sanitize(title).zip in roms/<system>/<bucket>/; a clash (any case) with another game gets " (2)"….
        var zipName = FileNameSanitizer.Sanitize(title) + ".zip";
        var folder = Path.Combine(paths.Value.Roms, system.Id, Bucket.For(zipName));
        Directory.CreateDirectory(folder);
        zipName = FileNameRules.FirstFreeIn(folder, zipName);
        var zipPath = $"{system.Id}/{Bucket.For(zipName)}/{zipName}";

        var entries = new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(bytes) };
        AddImages(entries, found.BoxArt, found.Snap);
        return await PackageAsync(file, null, zipPath, game, entries, result, actor, reviewAction, cancellationToken, afterMirror).ConfigureAwait(false);
    }

    private async Task<IngestItemResult> AddToGameAsync(
        IngestFile file, ISystemHandler system, Guid gameId, InfoJsonRom rom, DatMetadata metadata, byte[] bytes, CircuitBreaker breaker,
        IngestItemResult result, DateTimeOffset now, AuditActor actor, string? reviewAction, CancellationToken cancellationToken,
        Func<Guid, Task>? afterMirror = null)
    {
        var row = await games.GetAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (row is null)
            return IngestItemResult.Error(IngestErrors.TargetZipUnreadable, $"game {gameId} has ROM rows but no games row", system.Id, result.Sha1);

        var (existing, opened, error) = OpenTarget(row.ZipPath, system.Id, result.Sha1);
        if (error is not null)
            return error;
        var current = opened!;

        using (existing)
        {
            // §6.1 entry name clash: " (2)"… before the extension.
            rom = rom with { FileName = FileNameRules.FirstFree(rom.FileName, current.Roms.Select(r => r.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase).Contains) };
            var allRoms = PrimaryRelease.Apply([.. current.Roms, rom]);

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

            var entries = new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(bytes) };

            // Step 8 (I4): only while the game still has the placeholder, or no images at all.
            if (current.Images.Count == 0 || current.Images.Any(i => i.IsDefault))
            {
                var names = allRoms.OrderBy(PrimaryRelease.Rank, PrimaryReleaseRanker.Instance).Select(r => r.Identification.DatGameName);
                var found = await media.FindAsync(system, names, breaker, cancellationToken).ConfigureAwait(false);
                if (GameImages.ApplyFetched(current.Images, found, now) is var (images, imageEntries))
                {
                    game = game with { Images = images };
                    foreach (var (name, content) in imageEntries)
                        entries[LibraryZip.ImageEntry(name)] = () => new MemoryStream(content);
                }
            }

            // B9: the new release never adopts an orphan note of its name.
            return await PackageAsync(file, existing, row.ZipPath, game, entries, result with { GameId = gameId }, actor, reviewAction, cancellationToken,
                afterMirror, new HashSet<string>(StringComparer.Ordinal) { LibraryZip.NoteEntry(rom.FileName) }).ConfigureAwait(false);
        }
    }

    /// <summary>§6.1: every write starts from the ZIP's own <c>info.json</c>.</summary>
    internal (LibraryZip? Zip, InfoJsonGame? Game, IngestItemResult? Error) OpenTarget(string zipPath, string systemId, string? sha1)
    {
        var target = LibraryTarget.Open(paths.Value.Roms, zipPath, reader);
        return target.Code is null
            ? (target.Zip, target.Game, null)
            : (null, null, IngestItemResult.Error(target.Code, target.Details, systemId, sha1));
    }

    /// <summary>
    /// Step 3: the SHA1 is already in the library. Its entry has an open <c>HashMismatch</c>/<c>EntryMissing</c>/
    /// <c>EntrySizeMismatch</c> issue → rewrite it from this file and resolve the issue (<c>Repaired</c>, D30);
    /// otherwise <c>Duplicate</c> → <c>_duplicates/</c>.
    /// </summary>
    /// <param name="bytes">Null for an external input: only an embedded input can rebuild an embedded entry (I6).</param>
    private async Task<IngestItemResult> DuplicateOrRepairAsync(IngestFile file, RomRow known, byte[]? bytes, long size, string sha1, AuditActor actor,
        CancellationToken cancellationToken)
    {
        var game = await games.GetAsync(known.GameId, cancellationToken).ConfigureAwait(false);
        var entryName = known.ExternalFileName ?? LibraryZip.RomEntry(known.FileName);
        // shortcut: external self-repair (I6) is step 10; until then an external input or owner is always a Duplicate.
        var open = game is null || bytes is null || known.ExternalFileName is not null
            ? []
            : await issues.ListOpenAsync(game.ZipPath, entryName, IntegrityIssueType.SelfRepairable, cancellationToken).ConfigureAwait(false);

        if (open.Count == 0)
        {
            logger.LogWarning("{Path}: duplicate of {Zip} › {Entry} (sha1 {Sha1}, size {Size})",
                file.FullPath, game?.ZipPath, entryName, sha1, size);
            // An archive entry isn't moved on its own: the archive's fate covers it (§7.3).
            var moveError = file.ArchiveEntry is null ? MoveToDuplicates(file, actor) : null;
            return moveError ?? new IngestItemResult(IngestOutcome.Duplicate, Details: $"same as {game?.ZipPath} › {entryName}",
                SystemId: known.SystemId, GameId: known.GameId, Sha1: sha1);
        }

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
            return await PackageAsync(file, existing, game.ZipPath, repaired, entries, result, actor, null, cancellationToken,
                _ => issues.ResolveAsync(open.Select(i => i.Id).ToList(), now.UtcDateTime, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A file problem: <c>Failed</c>, and the file moves to <c>_failed/</c> with <c>&lt;name&gt;.error.txt</c> (spec §5).
    /// An archive entry stays put: its archive's fate decides (§7.3).
    /// </summary>
    public IngestItemResult FailFile(IngestFile file, AuditActor actor, string reason, string details, string? sha1 = null)
    {
        logger.LogWarning("{Path}: Failed {Reason}: {Details}", file.ArchiveEntry is null ? file.FullPath : $"{file.ArchivePath} › {file.ArchiveEntry}", reason, details);
        var moveError = file.ArchiveEntry is null ? MoveToFailed(file, actor, reason, details) : null;
        return moveError ?? new IngestItemResult(IngestOutcome.Failed, reason, details, file.SystemId, Sha1: sha1);
    }

    /// <summary>
    /// Moves the file to <c>_failed/</c> and writes its <c>.error.txt</c> (reason, details, job — or <c>review by
    /// &lt;username&gt;</c> without one — UTC time). Records <c>MovedToFailed</c>.
    /// </summary>
    /// <returns>An <c>Error IoError</c> result when the move failed, else null.</returns>
    public IngestItemResult? MoveToFailed(IngestFile file, AuditActor actor, string reason, string details)
    {
        var moved = MoveAside(file, StorageFolders.FailedFolder);
        if (moved.Error is not null)
            return moved.Error;
        var fromReview = actor.JobId is null;
        audit.Record(new AuditEvent(AuditActions.MovedToFailed, actor, file.SystemId, Path: auditPaths.Of(file.FullPath),
            TargetPath: auditPaths.Of(moved.Path), Details: fromReview ? "review dismiss" : reason));
        var by = actor.JobId is { } jobId ? jobId.ToString() : $"review by {actor.Username}";
        try
        {
            // External systems never overwrite: the name was reserved, so an occupant is someone else's file.
            using var diagnostic = new StreamWriter(new FileStream(moved.Path + DiagnosticSuffix, IsExternal(file) ? FileMode.CreateNew : FileMode.Create));
            diagnostic.Write($"reason: {reason}\ndetails: {details}\njob: {by}\ntime: {time.GetUtcNow().UtcDateTime:O}\nsource: {file.FullPath}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: couldn't write the .error.txt: {Error}", moved.Path, ex.Message);
        }
        return null;
    }

    /// <returns>An <c>Error IoError</c> result when the move failed, else null.</returns>
    public IngestItemResult? MoveToDuplicates(IngestFile file, AuditActor actor)
    {
        var moved = MoveAside(file, StorageFolders.DuplicatesFolder);
        if (moved.Error is null)
            audit.Record(new AuditEvent(AuditActions.MovedToDuplicates, actor, file.SystemId, Path: auditPaths.Of(file.FullPath),
                TargetPath: auditPaths.Of(moved.Path)));
        return moved.Error;
    }

    private const string DiagnosticSuffix = ".error.txt";

    private bool IsExternal(IngestFile file) => systems.ById(file.SystemId) is { EmbedRomsInZip: false };

    /// <summary>Moves the source into <c>ingest/&lt;system&gt;/&lt;folder&gt;/</c>, with a ` (N)` suffix on a clash (any case).</summary>
    private (string Path, IngestItemResult? Error) MoveAside(IngestFile file, string folder)
    {
        var directory = Path.Combine(Path.GetDirectoryName(file.FullPath)!, folder);
        if (IsExternal(file))
            return MoveAsideChecked(file, folder, directory);
        try
        {
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, FileNameRules.FirstFreeIn(directory, file.Name));
            File.Move(file.FullPath, target);
            return (target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ("", IngestItemResult.Error(IngestErrors.IoError, $"couldn't move to {folder}/: {ex.Message}", file.SystemId));
        }
    }

    /// <summary>
    /// W6 for an external system: the side folder and the source are checked (no links, a regular file), the name
    /// is budgeted with its staging and diagnostic names, and the move never replaces anything.
    /// </summary>
    private (string Path, IngestItemResult? Error) MoveAsideChecked(IngestFile file, string folder, string directory)
    {
        (string, IngestItemResult?) Fail(string? details) =>
            ("", IngestItemResult.Error(IngestErrors.IoError, $"couldn't move to {folder}/: {details}", file.SystemId));

        var root = paths.Value.Ingest;
        try
        {
            var target = ExternalRomPath.ClassifyDirectory(root, directory);
            if (target.Status == ExternalPathStatus.Missing)
            {
                Directory.CreateDirectory(target.Path);
                target = ExternalRomPath.ClassifyDirectory(root, directory);
            }
            if (target.Status != ExternalPathStatus.Found)
                return Fail(target.Details);
            var source = ExternalRomPath.ClassifyFile(root, file.FullPath);
            if (source.Status != ExternalPathStatus.Found)
                return Fail(source.Details);
            if (ExternalRomName.AllocateSidePath(target.Path, file.Name, folder == StorageFolders.FailedFolder ? DiagnosticSuffix : null) is not { } destination)
                return Fail($"no name for {file.Name} fits in {target.Path}");

            var moved = ExternalRomMove.Move(root, source.Path, root, destination, source.Snapshot!, logger);
            return moved.Status == ExternalMoveStatus.Moved ? (moved.Path!, null) : Fail(moved.Details);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// §8.4: an unmatched file whose extension doesn't fit its header classification is renamed in place to the
    /// classified handler's first extension (<c>Warn</c>).
    /// </summary>
    private IngestFile RenameToClassifiedExtension(IngestFile file, RomHeader? header, IReadOnlyList<ISystemHandler> family, AuditActor actor)
    {
        var classified = family.FirstOrDefault(h => h.Id == header?.ClassifiedSystemId);
        if (classified is null || classified.RomExtensions.Contains(Path.GetExtension(file.Name), StringComparer.OrdinalIgnoreCase))
            return file;
        var directory = Path.GetDirectoryName(file.FullPath)!;
        var renamed = FileNameRules.FirstFreeIn(directory, Path.GetFileNameWithoutExtension(file.Name) + classified.RomExtensions[0]);
        try
        {
            File.Move(file.FullPath, Path.Combine(directory, renamed));
            audit.Record(new AuditEvent(AuditActions.ExtensionRenamed, actor, file.SystemId, Path: auditPaths.Of(file.FullPath),
                TargetPath: auditPaths.Of(Path.Combine(directory, renamed))));
            logger.LogWarning("{Path}: header says {System}; renamed to {Name}", file.FullPath, classified.Id, renamed);
            return file with { FullPath = Path.Combine(directory, renamed), Name = renamed };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: couldn't rename to {Name}: {Error}", file.FullPath, renamed, ex.Message);
        }
        return file;
    }

    private async Task<IngestItemResult> QueueReviewAsync(IngestFile file, string originalName, string systemId, string reason,
        RomHashes hashes, long sizeBytes, RomHeader? header, Guid jobId, IReadOnlyList<Suggestion> suggestions,
        object? identification, IReadOnlyList<Guid>? candidates, CancellationToken cancellationToken)
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await reviews.UpsertAsync(new ReviewItem
        {
            SystemId = systemId, Reason = reason, SourcePath = file.SourcePath, ArchivePath = file.ArchivePath,
            ArchiveEntry = file.ArchiveEntry, OriginalFileName = originalName, SizeBytes = sizeBytes,
            Crc32 = hashes.Crc32, Md5 = hashes.Md5, Sha1 = hashes.Sha1,
            Header = header is null ? null : JsonSerializer.Serialize(header.Fields, json),
            Suggestions = JsonSerializer.Serialize(suggestions, json),
            DatIdentification = identification is null ? null : JsonSerializer.Serialize(identification, json),
            CandidateGameIds = candidates is null ? null : JsonSerializer.Serialize(candidates, json),
            CreatedAt = time.GetUtcNow().UtcDateTime, LastSeenJobId = jobId,
        }, cancellationToken).ConfigureAwait(false);
        return new IngestItemResult(IngestOutcome.Review, reason, SystemId: systemId, Sha1: hashes.Sha1);
    }

    /// <summary>Steps 9–11.</summary>
    /// <param name="afterMirror">Runs once the DB is updated, before the source is deleted.</param>
    /// <param name="reviewAction">Null from ingest; the resolution action from review (the <c>Ingested</c> row's details).</param>
    private async Task<IngestItemResult> PackageAsync(
        IngestFile file, LibraryZip? existing, string zipPath, InfoJsonGame game, Dictionary<string, Func<Stream>> entries,
        IngestItemResult result, AuditActor actor, string? reviewAction, CancellationToken cancellationToken,
        Func<Guid, Task>? afterMirror = null, IReadOnlySet<string>? removedEntries = null)
    {
        result = result with { GameId = game.GameId };
        var target = SafePath.Resolve(paths.Value.Roms, zipPath);
        try
        {
            await new LibraryZipWriter(reader).WriteAsync(target, existing, new LibraryZipChanges(game, entries, removedEntries), cancellationToken).ConfigureAwait(false);
        }
        catch (LibraryZipVerifyException ex)
        {
            logger.LogError(ex, "{Path}: verify failed writing {Zip}", file.FullPath, zipPath);
            return result with { Outcome = IngestOutcome.Error, Reason = IngestErrors.VerifyFailed, Details = ex.Message };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "{Path}: writing {Zip} failed", file.FullPath, zipPath);
            return result with { Outcome = IngestOutcome.Error, Reason = IngestErrors.IoError, Details = ex.Message };
        }
        // AU4: the ZIP is written, so the files changed even if the DB mirror fails below.
        var repaired = result.Outcome == IngestOutcome.Repaired;
        var release = entries.Keys.First(k => k.StartsWith(LibraryZip.RomsFolder, StringComparison.Ordinal))[LibraryZip.RomsFolder.Length..];
        audit.Record(new AuditEvent(repaired ? AuditActions.ZipRepaired : AuditActions.Ingested, actor, game.System, game.GameId,
            AuditSource(file), AuditPaths.Library(zipPath),
            repaired ? null : reviewAction is null ? $"release {release}" : $"review resolve ({reviewAction})"));

        var info = new FileInfo(target);
        try
        {
            await mirror.ReplaceAsync(new MirrorEntry(game, zipPath, info.Length, info.LastWriteTimeUtc), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Path}: {Zip} was written but the DB update failed", file.FullPath, zipPath);
            return result with { Outcome = IngestOutcome.Error, Reason = IngestErrors.DbWriteFailed, Details = ex.Message };
        }
        if (afterMirror is not null)
        {
            try { await afterMirror(game.GameId).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{Path}: post-mirror DB update failed", file.FullPath);
                return result with { Outcome = IngestOutcome.Error, Reason = IngestErrors.DbWriteFailed, Details = ex.Message };
            }
        }

        try
        {
            File.Delete(file.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Path}: ingested into {Zip} but couldn't be deleted ({Error}); the next run sees a duplicate", file.FullPath, zipPath, ex.Message);
        }
        logger.LogInformation("{Path}: {Outcome} into {Zip}", file.FullPath, result.Outcome, zipPath);
        return result;
    }

    private static void AddImages(Dictionary<string, Func<Stream>> entries, MediaImage? boxArt, MediaImage? snap)
    {
        foreach (var image in new[] { boxArt, snap }.OfType<MediaImage>())
            entries[LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(image.Bytes);
    }

    private sealed record RomHashes(string Crc32, string Md5, string Sha1)
    {
        public static RomHashes Of(byte[] bytes) =>
            new(Storage.Crc32.ToHex(bytes), Convert.ToHexStringLower(MD5.HashData(bytes)), Convert.ToHexStringLower(SHA1.HashData(bytes)));
    }
}
