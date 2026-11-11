using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>Synchronous review actions (§7.5); shares the ingest writer and the global job lock.</summary>
public sealed class ReviewResolver(JobLock jobLock, IIngestReviewRepository reviews, IGameRepository games, IRomRepository roms, DbSync dbSync,
    IngestPipeline pipeline, DatCache dats, ISystemRegistry systems, ArchiveExtractor extractor,
    IOptions<PathsOptions> paths, IAuditLog audit, AuditPaths auditPaths, TimeProvider time, ILogger<ReviewResolver> logger)
{
    /// <param name="actor">AU3: the signed-in user (no job); <see cref="AuditActor.UserId"/> is required.</param>
    public Task<ReviewResolutionResult> ResolveAsync(long id, ReviewResolutionRequest request, AuditActor actor, CancellationToken ct) =>
        ActAsync(id, request, actor, ct);

    public Task<ReviewResolutionResult> DismissAsync(long id, AuditActor actor, CancellationToken ct) =>
        ActAsync(id, null, actor, ct);

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
        AuditActor actor, int userId)
    {
        var now = time.GetUtcNow();
        var rom = new InfoJsonRom
        {
            FileName = filename, OriginalFileName = item.OriginalFileName, BaseTitle = title,
            Regions = regions, Languages = languages, Revision = revision, Flags = flags, IsPrimary = false,
            Identification = identification, Header = item.Header is null ? null : JsonNode.Parse(item.Header) as JsonObject,
            Metadata = new InfoJsonRomMetadata(item.SizeBytes, item.Crc32, item.Md5, item.Sha1, now, now, file.SourcePath),
        };
        var result = await pipeline.StoreReleaseAsync(file, system, rom, metadata, null, target,
            gameId => reviews.SetResolutionAsync(id, "Resolved", gameId, userId, time.GetUtcNow().UtcDateTime, CancellationToken.None),
            actor, action, CancellationToken.None, snapshot).ConfigureAwait(false);
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

    private async Task<ReviewResolutionResult> ActAsync(long id, ReviewResolutionRequest? request, AuditActor actor, CancellationToken ct)
    {
        var userId = actor.UserId!.Value; // review endpoints always have a signed-in user
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new("JobRunning");
        var item = await reviews.GetAsync(id, ct).ConfigureAwait(false);
        if (item is not { Status: "Pending" })
            return new("NotFound");
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
            if (request.Action == "Dat")
            {
                if (item.Reason != "NoDatMatch")
                    return ReviewResolutionResult.Invalid("action", "DAT selection requires a NoDatMatch item.");
                var chosen = systems.ById(request.DatSystem!);
                if (chosen is null || chosen.Family != system.Family)
                    return ReviewResolutionResult.Invalid("datSystem", "Choose a system in the source's family.");
                if (external && (!chosen.RomExtensions.Contains(originalExtension, StringComparer.OrdinalIgnoreCase)
                    || item.SizeBytes < chosen.MinRomSizeBytes || item.SizeBytes > chosen.MaxRomSizeBytes))
                    return ReviewResolutionResult.Invalid("datSystem", "That system can't store this file's extension or size.");
                // shortcut: TitleDb DAT picks are refused until step 24 resolves them (T9); never sent down the libretro path.
                if (chosen.DatSource == DatSource.TitleDb)
                    return ReviewResolutionResult.Invalid("datGameName", "Choosing a titledb entry isn't supported yet; use Manual.");
                system = chosen;
                var store = dats.For(system.Id);
                var datRom = store?.Roms.FirstOrDefault(r => r.GameName == request.DatGameName);
                if (datRom is null || store is null)
                    return ReviewResolutionResult.Invalid("datGameName", "Choose a game in the loaded DAT.");
                var parsed = NoIntroName.Parse(Path.GetFileNameWithoutExtension(datRom.RomName));
                title = parsed.BaseTitle; regions = parsed.Regions; languages = parsed.Languages; revision = parsed.Revision;
                flags = [.. parsed.Flags, "Unverified"];
                // G4: the DAT name's own extension (DAT entries are filtered to RomExtensions), e.g. an SMS-mode Game Gear .sms.
                filename = Path.GetFileNameWithoutExtension(datRom.RomName) + " (Unverified)" + (external ? originalExtension : Path.GetExtension(datRom.RomName));
                identification = new(IdentificationMethod.Manual, store.DatName, store.Version, datRom.GameName);
                metadata = store.MetadataFor(datRom.Crc32);
            }
            else if (request.Action == "Attach" && request.Flag == "None")
            {
                // shortcut: a queued TitleDb identity is attached by step 24 (T4); never through the libretro path.
                if (item.Reason == "AmbiguousGroup" && system.DatSource == DatSource.TitleDb)
                    return ReviewResolutionResult.Invalid("action", "Attaching a titledb match isn't supported yet; use Attach with a flag or Manual.");
                if (item.Reason != "AmbiguousGroup" ||
                    !(JsonSerializer.Deserialize<Guid[]>(item.CandidateGameIds ?? "[]") ?? []).Contains(request.TargetGameId!.Value))
                    return ReviewResolutionResult.Invalid("targetGameId", "Choose one of the ambiguous item's candidates.");
                var dat = JsonNode.Parse(item.DatIdentification!)!;
                // Keep the queued identity even when the loaded DATs have changed.
                identification = new(IdentificationMethod.Dat, dat["datName"]!.GetValue<string>(), dat["datVersion"]?.GetValue<string>(), dat["datGameName"]!.GetValue<string>());
                filename = Path.GetFileName(dat["romName"]!.GetValue<string>());
                var tags = NoIntroName.Parse(Path.GetFileNameWithoutExtension(filename));
                title = NoIntroName.Parse(dat["datGameName"]!.GetValue<string>()).BaseTitle;
                regions = tags.Regions; languages = tags.Languages; revision = tags.Revision; flags = tags.Flags;
                target = request.TargetGameId;
                metadata = dats.For(system.Id)?.MetadataFor(item.Crc32) ?? DatMetadata.Empty;
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
                }
                flags = [request.Flag!];
                filename = FileNameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(item.OriginalFileName)) + $" ({request.Flag})" + (external ? originalExtension : system.RomExtensions[0]);
            }
            else
            {
                title = request.Title!.Trim();
                regions = request.Regions!; languages = request.Languages!; flags = request.Flags!; revision = request.Revision;
                var stem = title + (regions.Count == 0 ? "" : $" ({string.Join(", ", regions)})") +
                    (string.IsNullOrEmpty(revision) ? "" : $" ({revision})") + string.Concat(flags.Select(f => $" ({f})"));
                filename = FileNameSanitizer.Sanitize(stem) + (external ? originalExtension : system.RomExtensions[0]);
            }

            if (filename.Contains("..", StringComparison.Ordinal))
                return ReviewResolutionResult.Invalid("title", "The generated ROM filename must not contain '..'; use Manual with another title.");
            if (external)
                filename = ExternalRomName.AllocateLogicalName(filename, new ExternalNameReservations([])); // E3: portable and within budget
            if (target is { } targetId)
            {
                var game = await games.GetAsync(targetId, ct).ConfigureAwait(false);
                if (game is null)
                    return new("NotFound");
                if (game.SystemId != system.Id)
                    return ReviewResolutionResult.Invalid("targetGameId", "Target must belong to the same system.");
            }
            if (external)
                return await ResolveExternalAsync(id, item, file, preparedSnapshot!, system, request.Action!, filename, title, regions,
                    languages, revision, flags, identification, metadata, target, actor, userId).ConfigureAwait(false);
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
}
