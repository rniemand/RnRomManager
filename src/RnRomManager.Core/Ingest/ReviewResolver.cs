using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>
/// Review actions (§7.5); shares the ingest writer and the global job lock. Resolve runs inside a
/// <see cref="ReviewResolveJob"/>, whose runner holds the lock (B2); Dismiss and Delete take it themselves.
/// </summary>
public sealed class ReviewResolver(JobLock jobLock, IIngestReviewRepository reviews, IGameRepository games, IRomRepository roms, DbSync dbSync,
    IngestPipeline pipeline, DatCache dats, ISystemRegistry systems, ArchiveExtractor extractor, ReviewSets sets,
    IOptions<PathsOptions> paths, IAuditLog audit, AuditPaths auditPaths, TimeProvider time, ILogger<ReviewResolver> logger)
{
    /// <summary>
    /// B2: resolves inside a job. The caller (the job runner) holds the lock; nothing here is cancellable. The result's
    /// <see cref="ReviewResolutionResult.Items"/> are the rows acted on: the item, or its set's members in set order
    /// (B14); empty when it no longer exists or isn't Pending.
    /// </summary>
    /// <param name="actor">The job's actor (AU3); <see cref="AuditActor.UserId"/> is required.</param>
    public async Task<ReviewResolutionResult> ResolveInJobAsync(long id, ReviewResolutionRequest request, AuditActor actor, JobProgress? progress)
    {
        var item = await reviews.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
        if (item is not { Status: "Pending" })
            return new("NotFound");
        // B14 step 1: a set member's set is found again with the lock held.
        var set = IsDismissible(item, systems) ? null : await sets.FindAsync(item, CancellationToken.None).ConfigureAwait(false);
        var items = set?.Select(m => m.Row).ToList() ?? [item];
        progress?.SetTotal(items.Count);
        if (set is null)
            progress?.SetCurrentItem(item.OriginalFileName); // a set's members are reported one by one as they are hashed
        var result = await ActCoreAsync(id, item, request, actor, CancellationToken.None, progress, set).ConfigureAwait(false);
        if (set is null)
            progress?.ItemDone();
        return result with { Items = items };
    }

    public async Task<ReviewResolutionResult> DismissAsync(long id, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new("JobRunning");
        var item = await reviews.GetAsync(id, ct).ConfigureAwait(false);
        if (item is not { Status: "Pending" })
            return new("NotFound");
        return await ActCoreAsync(id, item, null, actor, ct, null).ConfigureAwait(false);
    }

    /// <summary>B12/B15: a <c>.cue</c>/<c>.bin</c> item on a <c>FileSets</c> system whose set isn't found can't be resolved; no set member can be dismissed.</summary>
    public const string SetFileNotResolvable = "SetFileNotResolvable";

    /// <summary>
    /// B12: true for every item but a <c>.cue</c> or <c>.bin</c> (by its <c>originalFileName</c>, ignoring case) on a
    /// <c>FileSets</c> system, which is resolvable exactly when <see cref="ReviewSets.FindAsync"/> finds its set.
    /// </summary>
    public async Task<bool> IsResolvableAsync(ReviewItem item, CancellationToken cancellationToken = default) =>
        IsDismissible(item, systems) || await sets.FindAsync(item, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>B15: false for every <c>.cue</c>/<c>.bin</c> item on a <c>FileSets</c> system, so a set is never half dismissed.</summary>
    public static bool IsDismissible(ReviewItem item, ISystemRegistry systems) =>
        !(systems.ById(item.SystemId) is { FileSets: true }
            && Path.GetExtension(item.OriginalFileName).ToLowerInvariant() is ".cue" or ".bin");

    /// <summary>
    /// Hard delete (D2–D4): removes the source file from the ingest folder (the whole archive for an archive entry) and
    /// every pending review row of that source. A source that's already gone still removes the rows.
    /// </summary>
    public async Task<ReviewResolutionResult> DeleteSourceAsync(long id, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new("JobRunning");
        var item = await reviews.GetAsync(id, ct).ConfigureAwait(false);
        if (item is not { Status: "Pending" })
            return new("NotFound");
        if (IsExternal(item))
            return await DeleteExternalSourceAsync(id, item, actor).ConfigureAwait(false);
        var source = SafePath.Resolve(paths.Value.Ingest, Path.GetRelativePath(paths.Value.Ingest, item.ArchivePath ?? item.SourcePath));
        // AU4: File.Delete succeeds on a missing file, so only a file that existed gets a ReviewDeleted row.
        var existed = File.Exists(source);
        try
        {
            File.Delete(source); // no error when the file is already gone
        }
        catch (DirectoryNotFoundException)
        {
            // Gone along with its folder: the goal is reached.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Deleting review source '{Path}' (review item {ReviewId}) failed", source, id);
            return new("DeleteFailed");
        }
        if (existed)
            audit.Record(new AuditEvent(AuditActions.ReviewDeleted, actor, item.SystemId, Path: auditPaths.Of(source)));
        var removed = await reviews.DeletePendingBySourceAsync(item.SourcePath, item.ArchivePath, ct).ConfigureAwait(false);
        logger.LogInformation("User {UserId} deleted review item {ReviewId}: removed '{Path}' and {Rows} pending review row(s)",
            actor.UserId, id, source, removed);
        return new();
    }

    /// <summary>
    /// T9 for an external system, before any action-specific check: a loose source is used as it was classified; an
    /// archived item is re-extracted through one checked handle on its archive, whose snapshot is kept for I5.
    /// </summary>
    /// <returns>The file to resolve and its snapshot, or the failure code (the Pending row is kept).</returns>
    private (IngestFile? File, ExternalFileSnapshot? Snapshot, string? Code) PrepareExternal(long id, ReviewItem item, IngestFile file,
        string source, ExternalFileSnapshot snapshot, string work, ISystemHandler system)
    {
        file = file with { FullPath = source };
        if (item.ArchivePath is null)
            return (file, snapshot, null);

        var opened = ExternalRomPath.OpenRead(paths.Value.Ingest, source, FileShare.Read | FileShare.Delete, snapshot);
        if (opened.Status == ExternalPathStatus.Changed)
            return (null, null, "VerifyFailed");
        if (opened.Status != ExternalPathStatus.Found)
            return (null, null, "IoError");
        var workDir = ExternalRomPath.CreateDirectory(paths.Value.Cache, work);
        IReadOnlyList<ExtractedEntry> entries;
        using (var stream = opened.Stream!)
        {
            if (workDir.Status != ExternalPathStatus.Found)
                return (null, null, "IoError");
            var family = systems.ByFamily(system.Family);
            try
            {
                entries = extractor.Extract(stream, Path.GetFileName(source), workDir.Path,
                    ext => family.FirstOrDefault(h => h.RomExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))?.MaxRomSizeBytes);
            }
            catch (ArchiveOutputException ex)
            {
                logger.LogError(ex, "Review {Id}: extracting {Archive} failed", id, source);
                return (null, null, "IoError");
            }
            // The entries only count if the handle and the path still show the archive that was checked.
            var current = ExternalRomPath.ClassifyFile(paths.Value.Ingest, source);
            if (current.Status is not (ExternalPathStatus.Found or ExternalPathStatus.Missing))
                return (null, null, "IoError");
            if (current.Snapshot != snapshot || ExternalRomPath.SnapshotOf(stream) != snapshot)
                return (null, null, "VerifyFailed");
        }
        var entry = entries.FirstOrDefault(e => e.EntryPath == item.ArchiveEntry);
        if (entry is null)
            return (null, null, "VerifyFailed");
        var copy = ExternalRomPath.ClassifyFile(paths.Value.Cache, entry.ExtractedPath);
        if (copy.Status != ExternalPathStatus.Found)
            return (null, null, "IoError");
        return (file with { FullPath = copy.Path, ArchivePath = source, ArchiveEntry = item.ArchiveEntry, ArchiveSnapshot = snapshot }, copy.Snapshot, null);
    }

    /// <summary>
    /// T9 retry of an archived external item whose release may already be in the library (a mirror or resolution
    /// update failed after the ZIP was written, and the archive is still there). The library is synced first, then
    /// the SHA1's owner decides: the same archive with a verified file → <c>Resolved</c> with that game, writing
    /// nothing; another source or an embedded owner → <c>Duplicate</c>; an owner that can't be verified →
    /// <c>VerifyFailed</c>. Every refusal keeps the Pending row.
    /// </summary>
    /// <returns>null when the SHA1 has no owner: resolve as usual.</returns>
    private async Task<ReviewResolutionResult?> RecoverCommittedAsync(long id, ReviewItem item, IngestFile copy, ExternalFileSnapshot snapshot, int userId)
    {
        if (await Sha1Async(paths.Value.Cache, copy.FullPath, snapshot).ConfigureAwait(false) != item.Sha1)
            return new("VerifyFailed");
        RomRow? owner;
        try
        {
            await dbSync.SyncAllAsync(CancellationToken.None).ConfigureAwait(false);
            owner = await roms.GetBySha1Async(item.Sha1, CancellationToken.None).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException ex)
        {
            logger.LogError(ex, "Review {Id}: the library sync before the retry failed", id);
            return new("DbWriteFailed");
        }
        if (owner is null)
            return null;
        if (systems.ById(owner.SystemId) is not { EmbedRomsInZip: false })
            return new("Duplicate");

        var game = await games.GetAsync(owner.GameId, CancellationToken.None).ConfigureAwait(false);
        if (game is null)
            return new("VerifyFailed");
        var target = pipeline.OpenTargetChecked(game.ZipPath, owner.GameId, owner.SystemId);
        if (target.Code is not null)
        {
            logger.LogWarning("Review {Id}: the owner {Zip} of sha1 {Sha1} can't be verified: {Details}", id, game.ZipPath, item.Sha1, target.Details);
            return new("VerifyFailed");
        }
        using (target.Zip)
        {
            var rom = target.Game!.Roms.FirstOrDefault(r => r.Metadata.Sha1 == item.Sha1);
            if (rom?.ExternalFileName is null)
                return new("VerifyFailed");
            if (!string.Equals(rom.Metadata.SourcePath, copy.SourcePath, StringComparison.Ordinal))
                return new("Duplicate");
            var folder = Path.GetDirectoryName(SafePath.Resolve(paths.Value.Roms, game.ZipPath))!;
            var sidecar = ExternalRomPath.ClassifyFile(paths.Value.Roms, Path.Combine(folder, rom.ExternalFileName));
            if (sidecar.Status != ExternalPathStatus.Found || sidecar.Snapshot!.Length != rom.Metadata.SizeBytes
                || await Sha1Async(paths.Value.Roms, sidecar.Path, sidecar.Snapshot).ConfigureAwait(false) != item.Sha1)
                return new("VerifyFailed");
        }

        try
        {
            await reviews.SetResolutionAsync(id, "Resolved", owner.GameId, userId, time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Review {Id}: marking it Resolved failed", id);
            return new("DbWriteFailed");
        }
        logger.LogInformation("Review {Id}: its release was already in {Zip}; resolved without writing", id, game.ZipPath);
        return new(GameId: owner.GameId);
    }

    /// <returns>The SHA1 of the file <paramref name="snapshot"/> describes; null when it can't be read as that file.</returns>
    private static async Task<string?> Sha1Async(string root, string path, ExternalFileSnapshot snapshot)
    {
        var opened = ExternalRomPath.OpenRead(root, path, FileShare.Read | FileShare.Delete, snapshot);
        if (opened.Status != ExternalPathStatus.Found)
            return null;
        using var stream = opened.Stream!;
        var hashed = await RomFileHasher.HashAsync(stream, long.MaxValue, CancellationToken.None).ConfigureAwait(false);
        return ExternalRomPath.SnapshotOf(stream) == snapshot ? hashed?.Sha1 : null;
    }

    /// <summary>
    /// T9: the prepared file is re-hashed streamed (its SHA1 must still be the item's) and moved next to its ZIP
    /// (I4). Nothing is read into memory, and every failure keeps the Pending row.
    /// </summary>
    private async Task<ReviewResolutionResult> ResolveExternalAsync(long id, ReviewItem item, IngestFile file, ExternalFileSnapshot snapshot,
        ISystemHandler system, string action, string filename, string title, IReadOnlyList<string> regions, IReadOnlyList<string> languages,
        string? revision, IReadOnlyList<string> flags, InfoJsonIdentification identification, DatMetadata metadata, Guid? target,
        AuditActor actor, int userId, JobProgress? progress)
    {
        var now = time.GetUtcNow();
        var rom = new InfoJsonRom
        {
            FileName = filename, OriginalFileName = item.OriginalFileName, BaseTitle = title,
            Regions = regions, Languages = languages, Revision = revision, Flags = flags, IsPrimary = false,
                Version = ReleaseVersion.For(flags, item.OriginalFileName),
            Identification = identification, Header = item.Header is null ? null : JsonNode.Parse(item.Header) as JsonObject,
            Metadata = new InfoJsonRomMetadata(item.SizeBytes, item.Crc32, item.Md5, item.Sha1, now, now, file.SourcePath),
        };
        var result = await pipeline.StoreReleaseAsync(file, system, rom, metadata, null, target,
            gameId => reviews.SetResolutionAsync(id, "Resolved", gameId, userId, time.GetUtcNow().UtcDateTime, CancellationToken.None),
            actor, action, CancellationToken.None, snapshot, progress).ConfigureAwait(false);
        return result.Outcome == IngestOutcome.Ingested ? new(GameId: result.GameId) : new(result.Reason);
    }

    /// <summary>
    /// B14 steps 2 and 4: the set is stored as one release named by the action; every member's row is marked Resolved in
    /// one transaction once the mirror is written. A failure after the moves is <c>DbWriteFailed</c>, with the rows Pending.
    /// </summary>
    private async Task<ReviewResolutionResult> ResolveSetAsync(IReadOnlyList<(IngestFile File, ReviewItem Row)> set, ISystemHandler system,
        string action, string title, IReadOnlyList<string> regions, IReadOnlyList<string> languages, string? revision, IReadOnlyList<string> flags,
        InfoJsonIdentification identification, DatMetadata metadata, Guid? target, AuditActor actor, int userId, JobProgress? progress)
    {
        var rom = new InfoJsonRom
        {
            FileName = "", OriginalFileName = "", BaseTitle = title, Regions = regions, Languages = languages, Revision = revision, Flags = flags,
            IsPrimary = false, Identification = identification, Metadata = null!, // the pipeline fills names and metadata from the members
        };
        var ids = set.Select(m => m.Row.Id).ToList();
        var result = await pipeline.StoreSetReleaseAsync([.. set.Select(m => (m.File, m.Row.Sha1))], system, rom, metadata, target,
            gameId => reviews.SetResolutionAsync(ids, "Resolved", gameId, userId, time.GetUtcNow().UtcDateTime, CancellationToken.None),
            actor, action, progress ?? new JobProgress()).ConfigureAwait(false);
        return result.Outcome == IngestOutcome.Ingested ? new(GameId: result.GameId) : new(result.Reason);
    }

    private bool IsExternal(ReviewItem item) => systems.ById(item.SystemId) is { EmbedRomsInZip: false };

    /// <summary>
    /// I4 for an external system's source (the container for an archive entry): only a confirmed absence counts as
    /// gone. An unsafe path (a link on the way, a directory or special file) or an IO failure is <c>DeleteFailed</c>,
    /// with the file and its Pending rows kept. Once the file is deleted the rows are cleared even if the request ends.
    /// </summary>
    private async Task<ReviewResolutionResult> DeleteExternalSourceAsync(long id, ReviewItem item, AuditActor actor)
    {
        var check = ExternalRomPath.ClassifyFile(paths.Value.Ingest, item.ArchivePath ?? item.SourcePath);
        if (check.Status is not (ExternalPathStatus.Found or ExternalPathStatus.Missing))
        {
            logger.LogWarning("Review item {ReviewId}: its source {Path} can't be deleted safely: {Details}", id, check.Path, check.Details);
            return new("DeleteFailed");
        }
        if (check.Status == ExternalPathStatus.Found)
        {
            try
            {
                File.Delete(check.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Deleting review source '{Path}' (review item {ReviewId}) failed", check.Path, id);
                return new("DeleteFailed");
            }
            audit.Record(new AuditEvent(AuditActions.ReviewDeleted, actor, item.SystemId, Path: auditPaths.Of(check.Path)));
        }
        var removed = await reviews.DeletePendingBySourceAsync(item.SourcePath, item.ArchivePath, CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("User {UserId} deleted review item {ReviewId}: removed '{Path}' ({Status}) and {Rows} pending review row(s)",
            actor.UserId, id, check.Path, check.Status, removed);
        return new();
    }

    /// <summary>Resolve (<paramref name="request"/> set) or Dismiss of a Pending <paramref name="item"/>; the caller holds the lock.</summary>
    /// <param name="set">B14: the item's set, found under the lock; the whole set is resolved as one release.</param>
    private async Task<ReviewResolutionResult> ActCoreAsync(long id, ReviewItem item, ReviewResolutionRequest? request, AuditActor actor,
        CancellationToken ct, JobProgress? progress, IReadOnlyList<(IngestFile File, ReviewItem Row)>? set = null)
    {
        var userId = actor.UserId!.Value; // review actions always have a user: the signed-in one, or the job's requester
        if (!IsDismissible(item, systems) && (request is null || set is null))
            return new(SetFileNotResolvable);
        var external = IsExternal(item);
        string source;
        ExternalFileSnapshot? sourceSnapshot = null;
        if (external)
        {
            // I4: the source is classified before it is opened; only a confirmed absence is SourceMissing.
            var check = ExternalRomPath.ClassifyFile(paths.Value.Ingest, item.ArchivePath ?? item.SourcePath);
            if (check.Status is not (ExternalPathStatus.Found or ExternalPathStatus.Missing))
            {
                logger.LogWarning("Review item {ReviewId}: its source {Path} can't be used safely: {Details}", id, check.Path, check.Details);
                return new("IoError");
            }
            source = check.Path;
            sourceSnapshot = check.Snapshot;
            // T9: once started, an external resolution runs to the end even if the request goes away (a multi-GB
            // hash and move can outlast a reverse proxy's read timeout).
            if (check.Status == ExternalPathStatus.Found)
                ct = CancellationToken.None;
        }
        else
        {
            source = SafePath.Resolve(paths.Value.Ingest, Path.GetRelativePath(paths.Value.Ingest, item.ArchivePath ?? item.SourcePath));
        }
        if (!File.Exists(source))
        {
            await reviews.DeleteAsync([id], ct).ConfigureAwait(false);
            return new("SourceMissing");
        }
        var work = Path.Combine(paths.Value.Cache, CrashRecovery.WorkFolder, Guid.NewGuid().ToString());
        try
        {
            var file = new IngestFile(item.SystemId, source, item.ArchivePath is null ? Path.GetFileName(source) : item.OriginalFileName,
                item.SizeBytes, File.GetLastWriteTimeUtc(source));
            if (request is null)
            {
                if (item.ArchivePath is null && pipeline.MoveToFailed(file, actor, "Dismissed", "dismissed by an Admin") is { } failed)
                    return new(failed.Reason);
                // I4: an external source has moved, so its row is updated even if the request ends now.
                await reviews.SetResolutionAsync(id, "Dismissed", null, userId, time.GetUtcNow().UtcDateTime, external ? CancellationToken.None : ct).ConfigureAwait(false);
                return new();
            }

            var system = systems.ById(item.SystemId)!;
            // T9: an external release keeps its original extension; an embedded one takes the handler's first (its bytes may be normalised).
            var originalExtension = Path.GetExtension(item.OriginalFileName).ToLowerInvariant();
            ExternalFileSnapshot? preparedSnapshot = null;
            if (external)
            {
                (var prepared, preparedSnapshot, var refused) = PrepareExternal(id, item, file, source, sourceSnapshot!, work, system);
                if (refused is not null)
                    return new(refused);
                file = prepared!;
                if (item.ArchivePath is not null && await RecoverCommittedAsync(id, item, file, preparedSnapshot!, userId).ConfigureAwait(false) is { } recovered)
                    return recovered;
            }
            Guid? target = null;
            var metadata = DatMetadata.Empty;
            var identification = new InfoJsonIdentification(IdentificationMethod.Manual, null, null, null);
            string title;
            string filename;
            IReadOnlyList<string> regions = [], languages = [], flags = [];
            string? revision = null;
            if (request.Update == true && request.Action != "Dat")
                return ReviewResolutionResult.Invalid("update", ReviewResolutionRequest.UpdateNeedsDat);
            if (request.Disc is not null && !system.FileSets)
                return ReviewResolutionResult.Invalid("disc", "Disc numbers are for disc systems.");
            var discFlag = request.Disc is { } disc ? $"Disc {disc}" : null; // B10: the flag NoIntroName.Parse makes from a Redump name
            if (request.Action == "Dat")
            {
                if (set is not null)
                    return ReviewResolutionResult.Invalid("action", "Dat pick on a set isn't supported yet.");
                if (item.Reason != "NoDatMatch")
                    return ReviewResolutionResult.Invalid("action", "DAT selection requires a NoDatMatch item.");
                var chosen = systems.ById(request.DatSystem!);
                if (chosen is null || chosen.Family != system.Family)
                    return ReviewResolutionResult.Invalid("datSystem", "Choose a system in the source's family.");
                if (external && (!chosen.RomExtensions.Contains(originalExtension, StringComparer.OrdinalIgnoreCase)
                    || item.SizeBytes < chosen.MinRomSizeBytes || item.SizeBytes > chosen.MaxRomSizeBytes))
                    return ReviewResolutionResult.Invalid("datSystem", "That system can't store this file's extension or size.");
                if (request.Update == true && chosen.DatSource != DatSource.TitleDb)
                    return ReviewResolutionResult.Invalid("update", "Only a titledb game can be stored as an update.");
                system = chosen;
                var store = dats.For(system.Id);
                if (chosen.DatSource == DatSource.TitleDb)
                {
                    // T9: the picked title is named and grouped as T4 would have for its id, with N from the original
                    // file name; titledb never checks hashes, so it is as verified as an automatic match.
                    var pickedId = TitleDb.TitleIdOfGameName(request.DatGameName!);
                    if (request.Update == true && pickedId is not null)
                    {
                        // B8: a base pick becomes its update id; an update pick stays; anything else can't be an update.
                        pickedId = TitleDb.UpdateIdOf(pickedId);
                        if (pickedId is null)
                            return ReviewResolutionResult.Invalid("update", "Pick the base game or one of its updates to store this file as an update.");
                    }
                    var match = store is null || pickedId is null
                        || !store.Roms.Any(r => r.GameName == request.DatGameName)
                        ? null
                        : TitleDb.Match(pickedId, item.OriginalFileName, store.ByTitleId);
                    if (match is null)
                        return ReviewResolutionResult.Invalid("datGameName", "Choose a titledb game whose base game is in the loaded DAT.");
                    title = match.BaseTitle; languages = match.Languages; revision = match.Revision; flags = match.Flags;
                    filename = match.RomFileName;
                    identification = new(IdentificationMethod.TitleDb, store!.DatName, store.Version, match.GameName);
                    metadata = TitleDb.MetadataOf(match.Base);
                }
                else
                {
                    var datRom = store?.Roms.FirstOrDefault(r => r.GameName == request.DatGameName);
                    if (datRom is null || store is null)
                        return ReviewResolutionResult.Invalid("datGameName", "Choose a game in the loaded DAT.");
                    // B15: a FileSets game's DAT rom may be a "(Track 1).bin" data track, so its name and tags come from the game name.
                    var parsed = NoIntroName.Parse(chosen.FileSets ? datRom.GameName : Path.GetFileNameWithoutExtension(datRom.RomName));
                    title = parsed.BaseTitle; regions = parsed.Regions; languages = parsed.Languages; revision = parsed.Revision;
                    flags = [.. parsed.Flags, "Unverified"];
                    // G4: the DAT name's own extension (DAT entries are filtered to RomExtensions), e.g. an SMS-mode Game Gear .sms.
                    filename = chosen.FileSets
                        ? datRom.GameName + " (Unverified)" + originalExtension
                        : Path.GetFileNameWithoutExtension(datRom.RomName) + " (Unverified)" + (external ? originalExtension : Path.GetExtension(datRom.RomName));
                    identification = new(IdentificationMethod.Manual, store.DatName, store.Version, datRom.GameName);
                    metadata = store.MetadataFor(datRom.Crc32);
                }
            }
            else if (request.Action == "Attach" && request.Flag == "None")
            {
                if (item.Reason != "AmbiguousGroup" ||
                    !(JsonSerializer.Deserialize<Guid[]>(item.CandidateGameIds ?? "[]") ?? []).Contains(request.TargetGameId!.Value))
                    return ReviewResolutionResult.Invalid("targetGameId", "Choose one of the ambiguous item's candidates.");
                var dat = JsonNode.Parse(item.DatIdentification!)!;
                var datGameName = dat["datGameName"]!.GetValue<string>();
                filename = Path.GetFileName(dat["romName"]!.GetValue<string>());
                target = request.TargetGameId;
                if (dat["titleId"] is not null)
                {
                    // T4: the queued titledb identity, whole, so a refresh that drops the entry changes nothing.
                    identification = new(IdentificationMethod.TitleDb, dat["datName"]!.GetValue<string>(), dat["datVersion"]?.GetValue<string>(), datGameName);
                    title = dat["baseTitle"]!.GetValue<string>();
                    regions = Strings(dat["regions"]); languages = Strings(dat["languages"]); flags = Strings(dat["flags"]);
                    revision = dat["revision"]?.GetValue<string>();
                    // T7 from the base entry while the current DAT still has it.
                    metadata = TitleDb.TitleIdOfGameName(datGameName) is { } baseId && dats.For(system.Id)?.ByTitleId(baseId) is { } baseEntry
                        ? TitleDb.MetadataOf(baseEntry) : DatMetadata.Empty;
                }
                else
                {
                    // Keep the queued identity even when the loaded DATs have changed.
                    identification = new(IdentificationMethod.Dat, dat["datName"]!.GetValue<string>(), dat["datVersion"]?.GetValue<string>(), datGameName);
                    // B10: a FileSets release takes its tags from the DAT game name, as ingest does.
                    var tags = NoIntroName.Parse(system.FileSets ? datGameName : Path.GetFileNameWithoutExtension(filename));
                    title = NoIntroName.Parse(datGameName).BaseTitle;
                    regions = tags.Regions; languages = tags.Languages; revision = tags.Revision; flags = tags.Flags;
                    metadata = dats.For(system.Id)?.MetadataFor(item.Crc32) ?? DatMetadata.Empty;
                }
            }
            else if (request.Action == "Attach")
            {
                target = request.TargetGameId;
                var game = await games.GetAsync(target!.Value, ct).ConfigureAwait(false);
                if (game is null)
                    return new("NotFound");
                if (game.SystemId != system.Id)
                    return ReviewResolutionResult.Invalid("targetGameId", "Target must belong to the same system.");
                var (zip, current, error) = pipeline.OpenTarget(game.ZipPath, system.Id, item.Sha1);
                using (zip)
                {
                    if (error is not null)
                        return new(error.Reason);
                    title = current!.Title;
                    if (discFlag is not null && current.Roms.FirstOrDefault(r => r.IsPrimary) is { } primary)
                    {
                        // B11: disc 2 lines up with disc 1.
                        regions = primary.Regions; languages = primary.Languages; revision = primary.Revision;
                    }
                }
                var flag = discFlag ?? request.Flag!;
                flags = [flag];
                var stem = FileNameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(item.OriginalFileName));
                if (discFlag is null || !stem.Contains($"({discFlag})", StringComparison.OrdinalIgnoreCase))
                    stem += $" ({flag})";
                filename = stem + (external ? originalExtension : system.RomExtensions[0]);
            }
            else
            {
                title = request.Title!.Trim();
                regions = request.Regions!; languages = request.Languages!; flags = request.Flags!; revision = request.Revision;
                if (discFlag is not null)
                    flags = [.. flags.Where(f => !Regex.IsMatch(f, @"^Disc \d+$", RegexOptions.IgnoreCase)), discFlag];
                var stem = title + (regions.Count == 0 ? "" : $" ({string.Join(", ", regions)})") +
                    (string.IsNullOrEmpty(revision) ? "" : $" ({revision})") + string.Concat(flags.Select(f => $" ({f})"));
                filename = FileNameSanitizer.Sanitize(stem) + (external ? originalExtension : system.RomExtensions[0]);
            }

            // B12: a FileSets name may hold "..", as Redump names do ("...Iru!"); only "." or ".." or a separator is refused.
            // B14: a set keeps its members' source names, so the generated name isn't used.
            if (set is null && (system.FileSets ? filename is "." or ".." || filename.IndexOfAny(['/', '\\']) >= 0 : filename.Contains("..", StringComparison.Ordinal)))
                return ReviewResolutionResult.Invalid("title", "The generated ROM filename must not contain '..'; use Manual with another title.");
            if (external && !system.FileSets)
                filename = ExternalRomName.AllocateLogicalName(filename, new ExternalNameReservations([])); // E3: portable and within budget
            if (target is { } targetId)
            {
                var game = await games.GetAsync(targetId, ct).ConfigureAwait(false);
                if (game is null)
                    return new("NotFound");
                if (game.SystemId != system.Id)
                    return ReviewResolutionResult.Invalid("targetGameId", "Target must belong to the same system.");
            }
            if (set is not null)
                return await ResolveSetAsync(set, system, request.Action!, title, regions, languages, revision, flags, identification, metadata,
                    target, actor, userId, progress).ConfigureAwait(false);
            if (external)
                return await ResolveExternalAsync(id, item, file, preparedSnapshot!, system, request.Action!, filename, title, regions,
                    languages, revision, flags, identification, metadata, target, actor, userId, progress).ConfigureAwait(false);
            if (item.ArchivePath is not null)
            {
                var family = systems.ByFamily(systems.ById(item.SystemId)!.Family);
                var entries = extractor.Extract(source, work, ext => family.FirstOrDefault(h => h.RomExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))?.MaxRomSizeBytes);
                var entry = entries.FirstOrDefault(e => e.EntryPath == item.ArchiveEntry);
                if (entry is null)
                    return new("VerifyFailed");
                file = file with { FullPath = entry.ExtractedPath, ArchivePath = source, ArchiveEntry = item.ArchiveEntry };
            }
            await using var limited = new LimitStream(File.OpenRead(file.FullPath), system.MaxRomSizeBytes);
            using var buffer = new MemoryStream();
            await limited.CopyToAsync(buffer, ct).ConfigureAwait(false);
            var bytes = systems.ById(item.SystemId)!.NormalizeForHashing(buffer.ToArray()).ToArray();
            if (Convert.ToHexStringLower(SHA1.HashData(bytes)) != item.Sha1)
                return new("VerifyFailed");
            var now = time.GetUtcNow();
            var rom = new InfoJsonRom
            {
                FileName = filename, OriginalFileName = item.OriginalFileName, BaseTitle = title,
                Regions = regions, Languages = languages, Revision = revision, Flags = flags, IsPrimary = false,
                Version = ReleaseVersion.For(flags, item.OriginalFileName),
                Identification = identification, Header = item.Header is null ? null : JsonNode.Parse(item.Header) as JsonObject,
                Metadata = new InfoJsonRomMetadata(bytes.Length, item.Crc32, item.Md5, item.Sha1, now, now, file.SourcePath),
            };
            // Close the source before the pipeline deletes it (Windows FileShare semantics).
            await limited.DisposeAsync().ConfigureAwait(false);
            var result = await pipeline.StoreReleaseAsync(file, system, rom, metadata, bytes, target,
                gameId => reviews.SetResolutionAsync(id, "Resolved", gameId, userId, time.GetUtcNow().UtcDateTime, ct),
                actor, request.Action, ct).ConfigureAwait(false);
            return result.Outcome == IngestOutcome.Ingested ? new(GameId: result.GameId) : new(result.Reason);
        }
        catch (ArchiveRejectedException ex)
        {
            logger.LogWarning(ex, "Review {Id}: archive could not be re-extracted", id);
            return new("VerifyFailed");
        }
        catch (LimitExceededException) { return new("VerifyFailed"); }
        catch (System.Data.Common.DbException ex)
        {
            logger.LogError(ex, "Review {Id}: DB write failed", id);
            return new("DbWriteFailed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Review {Id}: IO failure", id);
            return new("IoError");
        }
        finally
        {
            // Never walk into a replacement of the work folder.
            if (ExternalRomPath.ClassifyDirectory(paths.Value.Cache, work).Status == ExternalPathStatus.Found)
            {
                try { Directory.Delete(work, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Review {Id}: couldn't remove work directory {Path}", id, work);
                }
            }
        }
    }

    private static IReadOnlyList<string> Strings(JsonNode? array) => array?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
}
