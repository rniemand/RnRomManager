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

/// <summary>Test seams of <see cref="IngestPipeline"/>'s external path: points where a test changes a file mid-ingest.</summary>
public sealed class ExternalIngestHooks
{
    /// <summary>Runs once an external source is open and checked, right before it is hashed.</summary>
    public Action<IngestFile>? BeforeHash { get; init; }

    /// <summary>Runs after media and naming, right before the source is checked again and moved.</summary>
    public Action<IngestFile>? BeforePlacement { get; init; }

    /// <summary>Passed to the move that places the ROM file next to its ZIP (not to a rollback).</summary>
    public ExternalMoveHooks? Move { get; init; }

    /// <summary>Passed to the move that sets a self-repair's incoming copy aside when its archive changed (I5).</summary>
    public ExternalMoveHooks? Recovery { get; init; }

    /// <summary>Passed to the ZIP write that follows the move.</summary>
    public LibraryZipWriterHooks? Writer { get; init; }
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
    /// <summary>Test seams of the external ingest path.</summary>
    public ExternalIngestHooks? ExternalHooks { get; set; }

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
        return await IdentifyAsync(file, folder, family, hashes, bytes.Length, bytes, bytes, null, jobId, actor, breaker, cancellationToken).ConfigureAwait(false);
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

        var (outcome, hashed, failure) = await HashCheckedAsync(file, root, fresh.Path, snapshot, byExtension.MaxRomSizeBytes, loose, cancellationToken)
            .ConfigureAwait(false);
        if (outcome == HashOutcome.Failed)
            return IngestItemResult.Error(IngestErrors.IoError, failure, file.SystemId);
        if (outcome == HashOutcome.Changed)
            return tooRecent; // still being copied in
        logger.LogDebug("{Path}: {Size} bytes streamed, crc32 {Crc32}, sha1 {Sha1}", file.FullPath, size, hashed!.Crc32, hashed.Sha1);

        // I3: only handlers that can store this extension and size identify it.
        var extension = Path.GetExtension(file.Name);
        var eligible = family.Where(h => h.RomExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            && size >= h.MinRomSizeBytes && size <= h.MaxRomSizeBytes).ToList();
        return await IdentifyAsync(file, folder, eligible, new RomHashes(hashed.Crc32, hashed.Md5, hashed.Sha1), size, hashed.Header, null,
            new ExternalSource(root, fresh.Path, snapshot), jobId, actor, breaker, cancellationToken).ConfigureAwait(false);
    }

    private enum HashOutcome
    {
        Hashed,

        /// <summary>The file changed: on its handle, at its path, or past the size cap while it was read.</summary>
        Changed,

        /// <summary>An IO failure, or the path is no longer a safe regular file.</summary>
        Failed,
    }

    /// <summary>
    /// I2/T9: one streamed read (CRC32, MD5, SHA1 and the header bytes) through a handle that must open
    /// <paramref name="snapshot"/>'s file, which must still be unchanged once it is read.
    /// </summary>
    /// <param name="recheckPath">Also require <paramref name="path"/> to still name that file afterwards.</param>
    private async Task<(HashOutcome Outcome, RomFileHashes? Hashes, string? Details)> HashCheckedAsync(IngestFile file, string root, string path,
        ExternalFileSnapshot snapshot, long maxBytes, bool recheckPath, CancellationToken cancellationToken)
    {
        var opened = ExternalRomPath.OpenRead(root, path, FileShare.ReadWrite | FileShare.Delete, snapshot);
        if (opened.Status == ExternalPathStatus.Changed)
            return (HashOutcome.Changed, null, opened.Details);
        if (opened.Status != ExternalPathStatus.Found)
            return (HashOutcome.Failed, null, opened.Details);
        using var stream = opened.Stream!;
        ExternalHooks?.BeforeHash?.Invoke(file);
        RomFileHashes? hashed;
        try
        {
            hashed = await RomFileHasher.HashAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (HashOutcome.Failed, null, ex.Message);
        }
        if (hashed is null || hashed.SizeBytes != snapshot.Length || ExternalRomPath.SnapshotOf(stream) != snapshot
            || (recheckPath && ExternalRomPath.ClassifyFile(root, path).Snapshot != snapshot))
            return (HashOutcome.Changed, null, $"{path} changed while it was read");
        return (HashOutcome.Hashed, hashed, null);
    }

    /// <summary>Steps 3–6 on hashed input.</summary>
    /// <param name="candidates">The handlers that may claim it: the whole family, or the eligible members for an external system.</param>
    /// <param name="headerBytes">What <c>ReadHeader</c> gets: the whole ROM, or its first MiB for an external one.</param>
    /// <param name="bytes">The ROM to store; null for an external ROM, which is never held in memory.</param>
    /// <param name="source">The checked file an external ROM is moved from; null for an embedded ROM.</param>
    private async Task<IngestItemResult?> IdentifyAsync(IngestFile file, ISystemHandler folder, IReadOnlyList<ISystemHandler> candidates,
        RomHashes hashes, long size, byte[] headerBytes, byte[]? bytes, ExternalSource? source, Guid jobId, AuditActor actor,
        CircuitBreaker breaker, CancellationToken cancellationToken)
    {
        var external = source is not null;
        // Step 3: global dedupe, self-repair, and review-item lifecycle.
        var review = await reviews.GetBySha1Async(hashes.Sha1, cancellationToken).ConfigureAwait(false);
        if (await roms.GetBySha1Async(hashes.Sha1, cancellationToken).ConfigureAwait(false) is { } known)
        {
            var duplicate = await DuplicateOrRepairAsync(file, known, bytes, source, size, hashes.Sha1, actor, cancellationToken).ConfigureAwait(false);
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
        // T4: a TitleDb system matches by title id (header, else file name); the others by SHA1.
        var match = candidates
            .Select(h => (Handler: h, Store: dats.For(h.Id)))
            .Select(x => (x.Handler, x.Store, Rom: x.Store?.BySha1(hashes.Sha1),
                Title: x.Handler.DatSource == DatSource.TitleDb && x.Store is { } titles
                    && TitleDb.TitleIdFrom(headers[x.Handler.Id]?.Fields, file.Name) is { } titleId
                    ? TitleDb.Match(titleId, file.Name, titles.ByTitleId) : null))
            .FirstOrDefault(x => x.Rom is not null || x.Title is not null);
        if (match.Rom is null && match.Title is null)
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
        var (system, store) = (match.Handler, match.Store!);
        if (system.Id != file.SystemId)
            logger.LogInformation("{Path}: dropped in ingest/{Folder} but it's a {System} ROM; stored under roms/{System}", file.FullPath, file.SystemId, system.Id, system.Id);

        var release = match.Title is { } title
            ? new Release(title.RomFileName, title.BaseTitle, title.GameName, [], title.Languages, title.Revision, title.Flags,
                IdentificationMethod.TitleDb, TitleDb.MetadataOf(title.Base), title.TitleId)
            : LibretroRelease(match.Rom!, store, file, external);
        // I3/E3: an external release's name is a portable logical name within budget.
        var romName = external ? ExternalRomName.AllocateLogicalName(release.FileName, new ExternalNameReservations([])) : release.FileName;

        // Step 6: group by system + base title.
        var baseTitle = release.BaseTitle;
        var owners = await roms.ListGameIdsByBaseTitleAsync(system.Id, baseTitle, cancellationToken).ConfigureAwait(false);
        if (owners.Count > 1)
        {
            // T4: a TitleDb row also keeps the whole identity, so Attach doesn't depend on the DAT loaded later.
            object queued = release.TitleId is null
                ? new { datName = store.DatName, datVersion = store.Version, datGameName = release.GameName, romName }
                : new
                {
                    datName = store.DatName, datVersion = store.Version, datGameName = release.GameName, romName, titleId = release.TitleId,
                    baseTitle, regions = release.Regions, languages = release.Languages, revision = release.Revision, flags = release.Flags,
                };
            return await QueueReviewAsync(file, file.Name, system.Id, "AmbiguousGroup", hashes, size, headers[system.Id], jobId,
                [], queued, owners, cancellationToken).ConfigureAwait(false);
        }

        var now = time.GetUtcNow();
        var rom = new InfoJsonRom
        {
            FileName = romName,
            OriginalFileName = file.Name,
            BaseTitle = baseTitle,
            Regions = release.Regions,
            Languages = release.Languages,
            Revision = release.Revision,
            Flags = release.Flags,
            IsPrimary = false,
            Identification = new InfoJsonIdentification(release.Method, store.DatName, store.Version, release.GameName),
            Header = headers[system.Id] is { } header ? JsonSerializer.SerializeToNode(header.Fields) as JsonObject : null,
            Metadata = new InfoJsonRomMetadata(size, hashes.Crc32, hashes.Md5, hashes.Sha1, now, now, file.SourcePath),
        };
        var metadata = release.Metadata;
        var result = new IngestItemResult(IngestOutcome.Ingested, SystemId: system.Id, Sha1: hashes.Sha1);

        return owners.Count == 0
            ? await NewGameAsync(file, system, rom, metadata, bytes, breaker, result, now, actor, null, cancellationToken, source: source).ConfigureAwait(false)
            : await AddToGameAsync(file, system, owners[0], rom, metadata, bytes, breaker, result, now, actor, null, cancellationToken, source: source).ConfigureAwait(false);
    }

    /// <summary>What a DAT match names a release: from a libretro DAT entry (by SHA1) or a titledb entry (T4/T5, by title id).</summary>
    /// <param name="FileName">Before E3 budgeting for an external system.</param>
    private sealed record Release(string FileName, string BaseTitle, string GameName, IReadOnlyList<string> Regions, IReadOnlyList<string> Languages,
        string? Revision, IReadOnlyList<string> Flags, IdentificationMethod Method, DatMetadata Metadata, string? TitleId);

    /// <summary>I3: an external release keeps its source's extension (lower-cased) on the DAT stem.</summary>
    private static Release LibretroRelease(DatRom datRom, DatStore store, IngestFile file, bool external)
    {
        var tags = NoIntroName.Parse(Path.GetFileNameWithoutExtension(datRom.RomName));
        var fileName = external ? Path.GetFileNameWithoutExtension(datRom.RomName) + Path.GetExtension(file.Name).ToLowerInvariant() : datRom.RomName;
        return new Release(fileName, NoIntroName.Parse(datRom.GameName).BaseTitle, datRom.GameName, tags.Regions, tags.Languages, tags.Revision,
            tags.Flags, IdentificationMethod.Dat, store.MetadataFor(datRom.Crc32), null);
    }

    /// <summary>Shared steps 6–11 for synchronous review resolution; Attach supplies a target and skips grouping.</summary>
    /// <param name="reviewAction">The resolution action (<c>Dat</c>, <c>Attach</c>, <c>Manual</c>), for the audit row's details.</param>
    /// <param name="bytes">The ROM of an embedded handler; null for an external one, whose file is re-hashed streamed and moved (T9).</param>
    /// <param name="snapshot">The external file as it was checked; required when <paramref name="bytes"/> is null.</param>
    public async Task<IngestItemResult> StoreReleaseAsync(IngestFile file, ISystemHandler system, InfoJsonRom rom,
        DatMetadata metadata, byte[]? bytes, Guid? targetGameId, Func<Guid, Task> afterMirror, AuditActor actor, string? reviewAction,
        CancellationToken cancellationToken, ExternalFileSnapshot? snapshot = null)
    {
        ExternalSource? source = null;
        if (bytes is null)
        {
            var root = file.ArchiveEntry is null ? paths.Value.Ingest : paths.Value.Cache;
            var (outcome, hashed, details) = await HashCheckedAsync(file, root, file.FullPath, snapshot!, system.MaxRomSizeBytes, recheckPath: true,
                cancellationToken).ConfigureAwait(false);
            if (outcome == HashOutcome.Failed)
                return IngestItemResult.Error(IngestErrors.IoError, details, system.Id, rom.Metadata.Sha1);
            if (outcome == HashOutcome.Changed || hashed!.Sha1 != rom.Metadata.Sha1)
            {
                return IngestItemResult.Error(IngestErrors.VerifyFailed, details ?? $"{file.FullPath}: sha1 {hashed!.Sha1}, not {rom.Metadata.Sha1}",
                    system.Id, rom.Metadata.Sha1);
            }
            rom = rom with { Metadata = rom.Metadata with { SizeBytes = hashed.SizeBytes } };
            source = new ExternalSource(root, file.FullPath, snapshot!);
        }

        var owners = targetGameId is { } target ? new[] { target } :
            await roms.ListGameIdsByBaseTitleAsync(system.Id, rom.BaseTitle, cancellationToken).ConfigureAwait(false);
        if (owners.Count > 1)
            return IngestItemResult.Error("AmbiguousGroup", "Several games own this base title; use Attach.");
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
    private async Task<IngestItemResult> NewGameAsync(
        IngestFile file, ISystemHandler system, InfoJsonRom rom, DatMetadata metadata, byte[]? bytes, CircuitBreaker breaker,
        IngestItemResult result, DateTimeOffset now, AuditActor actor, string? reviewAction, CancellationToken cancellationToken,
        Func<Guid, Task>? afterMirror = null, ExternalSource? source = null)
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
            var bucket = ExternalRomPath.ClassifyDirectory(library, Path.Combine(library, system.Id, Bucket.For(ExternalRomName.ZipName(title))));
            try
            {
                if (bucket.Status == ExternalPathStatus.Missing)
                {
                    Directory.CreateDirectory(bucket.Path);
                    bucket = ExternalRomPath.ClassifyDirectory(library, bucket.Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return IngestItemResult.Error(IngestErrors.IoError, $"{bucket.Path} can't be created: {ex.Message}", system.Id, result.Sha1);
            }
            if (bucket.Status != ExternalPathStatus.Found)
                return IngestItemResult.Error(IngestErrors.IoError, bucket.Details, system.Id, result.Sha1);

            var claims = await roms.ListExternalAsync(cancellationToken).ConfigureAwait(false);
            var externalZipPath = await ExternalRomName.AllocateZipPathAsync(library, system.Id, title, claims, games, cancellationToken).ConfigureAwait(false);
            var folderPath = externalZipPath[..externalZipPath.LastIndexOf('/')];
            var name = ExternalRomName.Allocate(Path.GetFileNameWithoutExtension(externalZipPath), rom.FileName,
                ExternalNameReservations.ForFolder(bucket.Path, folderPath, claims));
            game = game with { Roms = PrimaryRelease.Apply([rom with { ExternalFileName = name }], system.DatSource) };
            var images = new Dictionary<string, Func<Stream>>();
            AddImages(images, found.BoxArt, found.Snap);
            return await PackageAsync(file, null, externalZipPath, game, images, result, actor, reviewAction, rom.FileName, cancellationToken,
                afterMirror, placement: new ExternalPlacement(source, name)).ConfigureAwait(false);
        }

        // §6.1: Sanitize(title).zip in roms/<system>/<bucket>/; a clash (any case) with another game gets " (2)"….
        var zipName = FileNameSanitizer.Sanitize(title) + ".zip";
        var folder = Path.Combine(paths.Value.Roms, system.Id, Bucket.For(zipName));
        Directory.CreateDirectory(folder);
        zipName = FileNameRules.FirstFreeIn(folder, zipName);
        var zipPath = $"{system.Id}/{Bucket.For(zipName)}/{zipName}";

        var entries = new Dictionary<string, Func<Stream>> { [LibraryZip.RomEntry(rom.FileName)] = () => new MemoryStream(bytes!) };
        AddImages(entries, found.BoxArt, found.Snap);
        return await PackageAsync(file, null, zipPath, game, entries, result, actor, reviewAction, rom.FileName, cancellationToken, afterMirror).ConfigureAwait(false);
    }

    /// <param name="bytes">The ROM for the ZIP; null when <paramref name="source"/> is an external ROM's file.</param>
    private async Task<IngestItemResult> AddToGameAsync(
        IngestFile file, ISystemHandler system, Guid gameId, InfoJsonRom rom, DatMetadata metadata, byte[]? bytes, CircuitBreaker breaker,
        IngestItemResult result, DateTimeOffset now, AuditActor actor, string? reviewAction, CancellationToken cancellationToken,
        Func<Guid, Task>? afterMirror = null, ExternalSource? source = null)
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
            ExternalPlacement? placement = null;
            if (source is null)
            {
                // §6.1 entry name clash: " (2)"… before the extension.
                rom = rom with { FileName = FileNameRules.FirstFree(rom.FileName, current.Roms.Select(r => r.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase).Contains) };
            }
            else
            {
                // E3 logical name against the game's releases; E2 file name against the folder, every claim on it and this ZIP's own pointers.
                rom = rom with { FileName = ExternalRomName.AllocateLogicalName(rom.FileName, new ExternalNameReservations(current.Roms.Select(r => r.FileName))) };
                var library = paths.Value.Roms;
                var folder = ExternalRomPath.ClassifyDirectory(library, Path.GetDirectoryName(SafePath.Resolve(library, row.ZipPath))!);
                if (folder.Status != ExternalPathStatus.Found)
                    return IngestItemResult.Error(IngestErrors.IoError, folder.Details, system.Id, result.Sha1);
                var claims = await roms.ListExternalAsync(cancellationToken).ConfigureAwait(false);
                var name = ExternalRomName.Allocate(Path.GetFileNameWithoutExtension(row.ZipPath), rom.FileName,
                    ExternalNameReservations.ForFolder(folder.Path, row.ZipPath[..row.ZipPath.LastIndexOf('/')], claims,
                        current.Roms.Select(r => r.ExternalFileName).OfType<string>()));
                rom = rom with { ExternalFileName = name };
                placement = new ExternalPlacement(source, name);
            }
            var allRoms = PrimaryRelease.Apply([.. current.Roms, rom], system.DatSource);

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
            return await PackageAsync(file, existing, row.ZipPath, game, entries, result with { GameId = gameId }, actor, reviewAction, rom.FileName, cancellationToken,
                afterMirror, new HashSet<string>(StringComparer.Ordinal) { LibraryZip.NoteEntry(rom.FileName) }, placement).ConfigureAwait(false);
        }
    }

    /// <summary>§6.1: every write starts from the ZIP's own <c>info.json</c>.</summary>
    internal (LibraryZip? Zip, InfoJsonGame? Game, IngestItemResult? Error) OpenTarget(string zipPath, string systemId, string? sha1)
    {
        return Target(LibraryTarget.Open(paths.Value.Roms, zipPath, reader), systemId, sha1);
    }

    /// <summary>An external game's ZIP opened for checking (spec W1): it must be the game and system the mirror names.</summary>
    internal LibraryTarget OpenTargetChecked(string zipPath, Guid gameId, string systemId) =>
        LibraryTarget.OpenChecked(paths.Value.Roms, zipPath, reader, gameId, systemId);

    private static (LibraryZip? Zip, InfoJsonGame? Game, IngestItemResult? Error) Target(LibraryTarget target, string systemId, string? sha1) =>
        target.Code is null
            ? (target.Zip, target.Game, null)
            : (null, null, IngestItemResult.Error(target.Code, target.Details, systemId, sha1));

    /// <summary>
    /// Step 3: the SHA1 is already in the library. Its entry has an open <c>HashMismatch</c>/<c>EntryMissing</c>/
    /// <c>EntrySizeMismatch</c> issue → rewrite it from this file and resolve the issue (<c>Repaired</c>, D30);
    /// otherwise <c>Duplicate</c> → <c>_duplicates/</c>.
    /// </summary>
    /// <param name="bytes">The incoming embedded ROM; null for an external input.</param>
    /// <param name="source">The incoming external ROM's checked file; null for an embedded input.</param>
    private async Task<IngestItemResult> DuplicateOrRepairAsync(IngestFile file, RomRow known, byte[]? bytes, ExternalSource? source, long size,
        string sha1, AuditActor actor, CancellationToken cancellationToken)
    {
        var game = await games.GetAsync(known.GameId, cancellationToken).ConfigureAwait(false);
        var entryName = known.ExternalFileName ?? LibraryZip.RomEntry(known.FileName);
        IReadOnlyList<OpenIssue> open = [];
        // I6: only an input of the owner's storage mode repairs it; across modes the bytes on disk may differ.
        if (game is not null && (systems.ById(known.SystemId) is { EmbedRomsInZip: false }) == (source is not null))
        {
            if (source is null)
            {
                open = await issues.ListOpenAsync(game.ZipPath, entryName, IntegrityIssueType.SelfRepairable, cancellationToken).ConfigureAwait(false);
            }
            else if (known.ExternalFileName is { } pointer)
            {
                // The table's collation matches accent-distinct names too: only this exact file of this game counts.
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
            var moveError = file.ArchiveEntry is null ? MoveToDuplicates(file, actor) : null;
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
            return await PackageAsync(file, existing, game.ZipPath, repaired, entries, result, actor, null, known.FileName, cancellationToken,
                _ => issues.ResolveAsync(open.Select(i => i.Id).ToList(), now.UtcDateTime, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// I6 for an external ROM: the incoming file is moved over the release's <c>externalFileName</c> and the ZIP
    /// rewritten. A file still at that name is set aside under a reserved <c>.repair.tmp</c> name first and only
    /// removed once the ZIP is written; every failure puts both files back.
    /// </summary>
    private async Task<IngestItemResult> RepairExternalAsync(IngestFile file, RomRow known, GameRow game, IReadOnlyList<OpenIssue> open,
        ExternalSource source, string sha1, AuditActor actor, CancellationToken cancellationToken)
    {
        var library = paths.Value.Roms;
        var (existing, current, error) = Target(LibraryTarget.OpenChecked(library, game.ZipPath, reader, known.GameId, known.SystemId), known.SystemId, sha1);
        if (error is not null)
            return error;
        using (existing)
        {
            IngestItemResult Fail(string code, string? details) => IngestItemResult.Error(code, details, known.SystemId, sha1);
            var rom = current!.Roms.FirstOrDefault(r => r.Metadata.Sha1 == sha1);
            if (rom?.ExternalFileName is not { } pointer || !string.Equals(pointer, known.ExternalFileName, StringComparison.Ordinal))
            {
                return Fail(IngestErrors.TargetZipUnreadable, $"{game.ZipPath}: the mirror says {known.ExternalFileName} holds sha1 {sha1}, " +
                    $"but info.json says {rom?.ExternalFileName ?? "no release has it"}; the mirror is stale");
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
                current.Roms.Select(r => r.ExternalFileName).OfType<string>());
            var plan = new RepairPlan(
                there.Status == ExternalPathStatus.Found ? there.Path : null, there.Snapshot,
                Path.Combine(folder.Path, ExternalRomName.AllocateRepairBackup(pointer, names)),
                Path.Combine(folder.Path, ExternalRomName.Allocate(Path.GetFileNameWithoutExtension(game.ZipPath), rom.FileName, names)));

            var now = time.GetUtcNow();
            var repaired = current with
            {
                Roms = current.Roms.Select(r => r.Metadata.Sha1 == sha1 ? r with { Metadata = r.Metadata with { DateModified = now } } : r).ToList(),
                Metadata = current.Metadata with { UpdatedAt = now, AppVersion = AppInfo.Version },
            };
            var result = new IngestItemResult(IngestOutcome.Repaired, Details: $"rewrote {game.ZipPath} › {pointer} ({string.Join(", ", open.Select(i => i.Type))})",
                SystemId: known.SystemId, GameId: known.GameId, Sha1: sha1);
            return await PackageAsync(file, existing, game.ZipPath, repaired, [], result, actor, null, rom.FileName, cancellationToken,
                _ => issues.ResolveAsync(open.Select(i => i.Id).ToList(), now.UtcDateTime, CancellationToken.None),
                placement: new ExternalPlacement(source, pointer), repair: plan).ConfigureAwait(false);
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

            var moved = ExternalRomMove.Move(root, source.Path, destination, source.Snapshot!, logger);
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

    /// <summary>The checked file an external ROM is hashed from and moved from (I2, I4).</summary>
    /// <param name="Root">The storage root it was checked under: ingest, or the cache for an extracted archive entry.</param>
    private sealed record ExternalSource(string Root, string Path, ExternalFileSnapshot Snapshot);

    /// <summary>An external ROM's file and the name it takes next to its ZIP (E1).</summary>
    private sealed record ExternalPlacement(ExternalSource Source, string ExternalFileName);

    /// <summary>I6: the file at a repaired ROM's pointer, and the reserved names a repair sets files aside under.</summary>
    /// <param name="ExistingPath">The file at the pointer as spelled on disk; null when it is missing.</param>
    /// <param name="BackupPath">Where that file waits until the ZIP is written (<c>.repair.tmp</c>).</param>
    /// <param name="OrphanPath">An unlisted sibling for the incoming copy when its archive changed (I5).</param>
    private sealed record RepairPlan(string? ExistingPath, ExternalFileSnapshot? Snapshot, string BackupPath, string OrphanPath);

    /// <summary>Steps 9–11.</summary>
    /// <param name="afterMirror">Runs once the DB is updated, before the source is deleted.</param>
    /// <param name="reviewAction">Null from ingest; the resolution action from review (the <c>Ingested</c> row's details).</param>
    /// <param name="release">The new release's <c>fileName</c>: the <c>Ingested</c> row's details (I7).</param>
    /// <param name="placement">An external ROM's file, moved next to the ZIP right before the ZIP is written (I4).</param>
    private async Task<IngestItemResult> PackageAsync(
        IngestFile file, LibraryZip? existing, string zipPath, InfoJsonGame game, Dictionary<string, Func<Stream>> entries,
        IngestItemResult result, AuditActor actor, string? reviewAction, string release, CancellationToken cancellationToken,
        Func<Guid, Task>? afterMirror = null, IReadOnlySet<string>? removedEntries = null, ExternalPlacement? placement = null,
        RepairPlan? repair = null)
    {
        result = result with { GameId = game.GameId };
        var library = paths.Value.Roms;
        var target = SafePath.Resolve(library, zipPath);
        ExternalMoveResult? moved = null;
        ExternalFileSnapshot? published = null;
        if (placement is not null)
        {
            if (repair?.ExistingPath is not null && StepAside(repair) is { } refused)
                return IngestItemResult.Error(IngestErrors.IoError, refused, result.SystemId, result.Sha1);
            (moved, var stop) = Place(file, placement, target, result);
            if (stop is not null)
                return stop with { Details = Join(stop.Details, RestoreOriginal(repair)) };
            // From the move on nothing may strand the file: the write, the mirror and afterMirror run to the end (I4).
            cancellationToken = CancellationToken.None;
        }

        try
        {
            var placed = moved is null ? null
                : new Dictionary<string, ExternalFileSnapshot>(StringComparer.OrdinalIgnoreCase) { [placement!.ExternalFileName] = moved.Snapshot! };
            var changes = new LibraryZipChanges(game, entries, removedEntries, placed);
            if (placement is null)
                await new LibraryZipWriter(reader).WriteAsync(target, existing, changes, cancellationToken).ConfigureAwait(false);
            else
                published = await new LibraryZipWriter(reader).WriteCheckedAsync(library, target, existing, changes, cancellationToken, ExternalHooks?.Writer).ConfigureAwait(false);
        }
        catch (Exception ex) when (placement is not null || ex is LibraryZipVerifyException or IOException or UnauthorizedAccessException)
        {
            // I4: any failure of the write puts an external ROM's file back before it is reported.
            var (survivor, restoredAs) = placement is null ? (null, null) : RollBack(file, placement, moved!, repair);
            var reason = ex switch
            {
                LibraryZipVerifyException => IngestErrors.VerifyFailed,
                IOException or UnauthorizedAccessException => IngestErrors.IoError,
                _ => null,
            };
            logger.LogError(ex, "{Path}: writing {Zip} failed", file.FullPath, zipPath);
            if (restoredAs is not null && await RetargetPendingAsync(placement!.Source.Path, restoredAs, result, ex.Message).ConfigureAwait(false) is { } retargetFailed)
                return retargetFailed;
            if (reason is null)
                throw;
            return result with { Outcome = IngestOutcome.Error, Reason = reason, Details = survivor is null ? ex.Message : $"{ex.Message}; {survivor}" };
        }
        if (repair is not null)
            DeleteBackup(repair);

        // AU4: the ZIP is written, so the files changed even if the DB mirror fails below.
        var repaired = result.Outcome == IngestOutcome.Repaired;
        audit.Record(new AuditEvent(repaired ? AuditActions.ZipRepaired : AuditActions.Ingested, actor, game.System, game.GameId,
            AuditSource(file), AuditPaths.Library(zipPath),
            repaired ? null : reviewAction is null ? $"release {release}" : $"review resolve ({reviewAction})"));

        try
        {
            MirrorEntry entry;
            if (published is not null)
            {
                // W1: stamped from the ZIP this write published, and only while the path still names it.
                if (ExternalRomPath.ClassifyFile(library, target).Snapshot != published)
                    throw new IOException($"{zipPath} is no longer the ZIP this write published");
                entry = new MirrorEntry(game, zipPath, published.Length, published.LastWriteTimeUtc);
            }
            else
            {
                var info = new FileInfo(target);
                entry = new MirrorEntry(game, zipPath, info.Length, info.LastWriteTimeUtc);
            }
            await mirror.ReplaceAsync(entry, cancellationToken).ConfigureAwait(false);
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

        if (placement is not null)
        {
            // I5: the source was moved, so there is nothing left to delete.
            logger.LogInformation("{Path}: {Outcome} into {Zip} as {File}", file.FullPath, result.Outcome, zipPath, placement.ExternalFileName);
            return result;
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

    /// <summary>
    /// I4: verified loose bytes went back under another ingest name, so a Pending row of the original path and SHA1
    /// follows them; its original file name and queued identification are kept. This runs to the end whatever the
    /// caller's token says, under the lock the caller already holds.
    /// </summary>
    /// <returns>A <c>DbWriteFailed</c> result when the row couldn't be moved; null when it was (or there was none).</returns>
    private async Task<IngestItemResult?> RetargetPendingAsync(string original, string restoredAs, IngestItemResult result, string writeFailure)
    {
        try
        {
            await reviews.RetargetPendingAsync(original, result.Sha1!, restoredAs, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Original}: the ZIP write failed ({WriteFailure}) and the file went back as {Restored}, but its Pending row still names " +
                "the original path. Once that path is free, move {Restored} back to {Original}.", original, writeFailure, restoredAs, restoredAs, original);
            return result with
            {
                Outcome = IngestOutcome.Error, Reason = IngestErrors.DbWriteFailed,
                Details = $"{writeFailure}; moved back as {restoredAs}, but the Pending row still names {original}: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// I4: moves an external ROM's file to its name next to <paramref name="target"/>. The source must still be the
    /// file that was hashed; one that changed since is <c>Skipped</c> <c>TooRecent</c> and stays where it is.
    /// </summary>
    /// <returns>The move, or the item's result when nothing was moved.</returns>
    private (ExternalMoveResult? Moved, IngestItemResult? Stop) Place(IngestFile file, ExternalPlacement placement, string target, IngestItemResult result)
    {
        var source = placement.Source;
        IngestItemResult Io(string? details) => IngestItemResult.Error(IngestErrors.IoError, details, result.SystemId, result.Sha1);
        var tooRecent = new IngestItemResult(IngestOutcome.Skipped, IngestReason.TooRecent, SystemId: file.SystemId, Sha1: result.Sha1);

        ExternalHooks?.BeforePlacement?.Invoke(file);
        var current = ExternalRomPath.ClassifyFile(source.Root, source.Path);
        if (current.Status == ExternalPathStatus.Found && current.Snapshot != source.Snapshot)
        {
            logger.LogInformation("{Path}: changed since it was hashed; left for a later run", file.FullPath);
            return (null, tooRecent);
        }
        if (current.Status != ExternalPathStatus.Found)
            return (null, Io(current.Details));
        var library = paths.Value.Roms;
        var folder = ExternalRomPath.ClassifyDirectory(library, Path.GetDirectoryName(target)!);
        if (folder.Status != ExternalPathStatus.Found)
            return (null, Io(folder.Details));

        ExternalMoveResult moved;
        try
        {
            moved = ExternalRomMove.Move(source.Root, current.Path, Path.Combine(folder.Path, placement.ExternalFileName), source.Snapshot,
                logger, result.Sha1, ExternalHooks?.Move);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, Io(ex.Message));
        }
        switch (moved.Status)
        {
            case ExternalMoveStatus.Moved:
                return (moved, null);
            case ExternalMoveStatus.SourceChanged:
                logger.LogInformation("{Path}: changed while it was moved ({Details}); left for a later run", file.FullPath, moved.Details);
                return (null, tooRecent);
            default:
                var elsewhere = moved.SurvivingPath is { } survivor && survivor != current.Path;
                return (null, Io(elsewhere ? $"{moved.Details}; the complete copy is at {moved.SurvivingPath}" : moved.Details));
        }
    }

    /// <summary>
    /// I4/I5 after a failed ZIP write: a loose file goes back to its ingest path (or a free sibling name); an archive
    /// entry's copy is deleted only while the archive still holds those bytes, else it is kept as an orphan (beside the
    /// pointer, for a repair). A repair's original file then goes back to its own name, once the pointer is free.
    /// </summary>
    /// <returns>Where the bytes are, for the item's details; null when everything is back exactly where it was.</returns>
    /// <returns>
    /// Where the bytes are, for the item's details (null when everything is back exactly where it was), and the
    /// ingest path a loose source was fully restored under when its own name was taken.
    /// </returns>
    private (string? Note, string? RestoredAs) RollBack(IngestFile file, ExternalPlacement placement, ExternalMoveResult moved, RepairPlan? repair)
    {
        var (note, pointerFree, restoredAs) = RemoveIncoming(file, placement, moved, repair);
        if (repair?.ExistingPath is null)
            return (note, restoredAs);
        if (!pointerFree)
        {
            logger.LogError("{Original}: kept at {Backup}, since the incoming copy still holds its name", repair.ExistingPath, repair.BackupPath);
            return (Join(note, $"the original file was kept at {repair.BackupPath}"), restoredAs);
        }
        return (Join(note, RestoreOriginal(repair)), restoredAs);
    }

    /// <returns>A note for the details, and whether the placed copy is gone from its library name.</returns>
    private (string? Note, bool PointerFree, string? RestoredAs) RemoveIncoming(IngestFile file, ExternalPlacement placement, ExternalMoveResult moved, RepairPlan? repair)
    {
        var library = paths.Value.Roms;
        var placed = moved.Path!;
        if (file.ArchiveEntry is not null)
        {
            var archive = file.ArchiveSnapshot is null ? null : ExternalRomPath.ClassifyFile(paths.Value.Ingest, file.ArchivePath!);
            if (archive is not { Status: ExternalPathStatus.Found } || archive.Snapshot != file.ArchiveSnapshot)
            {
                if (repair is null)
                {
                    logger.LogError("{Archive} › {Entry}: the archive changed, so the verified copy is kept at {Orphan}", file.ArchivePath, file.ArchiveEntry, placed);
                    return ($"the archive changed, so the verified copy was kept at {placed}", false, null);
                }
                // I5: the pointer belongs to the original file, so the verified copy steps aside to an unlisted name.
                var aside = ExternalRomMove.Move(library, placed, repair.OrphanPath, moved.Snapshot!, logger, hooks: ExternalHooks?.Recovery);
                if (aside.Status != ExternalMoveStatus.Moved)
                {
                    var at = aside.SurvivingPath ?? placed;
                    logger.LogError("{Archive} › {Entry}: the archive changed and the verified copy couldn't be set aside ({Details}); it is at {At}",
                        file.ArchivePath, file.ArchiveEntry, aside.Details, at);
                    return ($"the archive changed and setting the verified copy aside failed ({aside.Details}); it is at {at}", false, null);
                }
                logger.LogError("{Archive} › {Entry}: the archive changed, so the verified copy is kept at {Orphan}", file.ArchivePath, file.ArchiveEntry, repair.OrphanPath);
                return ($"the archive changed, so the verified copy was kept at {repair.OrphanPath}", true, null);
            }
            if (ExternalRomPath.ClassifyFile(library, placed).Snapshot != moved.Snapshot)
            {
                logger.LogError("{Orphan} is no longer the copy that was placed; it was left as it is", placed);
                return ($"{placed} is no longer the copy that was placed; it was left as it is", false, null);
            }
            try
            {
                File.Delete(placed);
                return (null, true, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("{Orphan}: the copy couldn't be removed: {Error}", placed, ex.Message);
                return ($"the copy at {placed} couldn't be removed", false, null);
            }
        }

        var original = placement.Source.Path;
        var back = ExternalRomName.AllocateRollbackPath(original, fitSourcePathColumn: true);
        if (back is null)
        {
            logger.LogError("{Path}: no free name to move it back to; the complete copy is at {Placed}", original, placed);
            return ($"no free name to move it back to; the complete copy is at {placed}", false, null);
        }
        var restored = ExternalRomMove.Move(library, placed, back, moved.Snapshot!, logger);
        switch (restored.Status)
        {
            case ExternalMoveStatus.Moved when back == original:
                return (null, true, null);
            case ExternalMoveStatus.Moved:
                logger.LogWarning("{Path}: its name was taken, so it was moved back as {Restored}", original, back);
                return ($"moved back as {back}", true, back);
            case ExternalMoveStatus.SourceChanged:
                logger.LogError("{Placed} was replaced before it could be moved back; it was left as it is", placed);
                return ($"{placed} was replaced before it could be moved back; it was left as it is", false, null);
            default:
                // Never name a path as the copy unless the move vouches for it.
                var where = restored.SurvivingPath is { } survivor ? $"the complete copy is at {survivor}" : "the owned copy could not be found";
                logger.LogError("{Path}: moving it back failed ({Details}); {Where}", original, restored.Details, where);
                return ($"moving it back failed ({restored.Details}); {where}", false, null);
        }
    }

    /// <summary>I6: sets the file at the pointer aside under its reserved backup name.</summary>
    /// <returns>Why it wasn't, with nothing changed; null when it was.</returns>
    private string? StepAside(RepairPlan repair)
    {
        var library = paths.Value.Roms;
        var current = ExternalRomPath.ClassifyFile(library, repair.ExistingPath!);
        if (current.Status != ExternalPathStatus.Found || current.Snapshot != repair.Snapshot)
            return $"{repair.ExistingPath} changed before it could be repaired: {current.Details ?? "it was replaced"}";
        var renamed = NoReplaceRename.Move(current.Path, repair.BackupPath);
        if (!renamed.Renamed)
            return $"{repair.ExistingPath} couldn't be set aside as {repair.BackupPath}: {renamed.Details}";
        return ExternalRomPath.ClassifyFile(library, repair.BackupPath).Snapshot == repair.Snapshot
            ? null
            : $"the file set aside at {repair.BackupPath} is not the one that was at {repair.ExistingPath}; nothing was repaired";
    }

    /// <summary>I6: puts the set-aside original back under its own spelling; nothing when there was none.</summary>
    /// <returns>A note when it couldn't be put back; null otherwise.</returns>
    private string? RestoreOriginal(RepairPlan? repair)
    {
        if (repair?.ExistingPath is null)
            return null;
        var backup = ExternalRomPath.ClassifyFile(paths.Value.Roms, repair.BackupPath);
        if (backup.Status == ExternalPathStatus.Missing && ExternalRomPath.ClassifyFile(paths.Value.Roms, repair.ExistingPath).Snapshot == repair.Snapshot)
            return null; // it was never set aside
        if (backup.Status != ExternalPathStatus.Found || backup.Snapshot != repair.Snapshot)
        {
            logger.LogError("{Backup}: no longer the original file of {Original}; it was left as it is", repair.BackupPath, repair.ExistingPath);
            return $"{repair.BackupPath} is no longer the original file; it was left as it is";
        }
        var back = NoReplaceRename.Move(backup.Path, repair.ExistingPath);
        if (back.Renamed)
            return null;
        logger.LogError("{Original}: couldn't be put back ({Details}); it is at {Backup}", repair.ExistingPath, back.Details, repair.BackupPath);
        return $"putting the original file back failed ({back.Details}); it is at {repair.BackupPath}";
    }

    /// <summary>I6 after the ZIP is written: the set-aside original is removed, unless something else took its name.</summary>
    private void DeleteBackup(RepairPlan repair)
    {
        if (repair.ExistingPath is null)
            return;
        try
        {
            if (ExternalRomPath.ClassifyFile(paths.Value.Roms, repair.BackupPath) is { Status: ExternalPathStatus.Found } backup && backup.Snapshot == repair.Snapshot)
                File.Delete(backup.Path);
            else
                logger.LogWarning("{Backup}: not the file the repair set aside; left as it is", repair.BackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Backup}: couldn't be removed after the repair ({Error}); integrity reports it", repair.BackupPath, ex.Message);
        }
    }

    private static string? Join(params string?[] parts) =>
        parts.OfType<string>().ToList() is { Count: > 0 } notes ? string.Join("; ", notes) : null;

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
