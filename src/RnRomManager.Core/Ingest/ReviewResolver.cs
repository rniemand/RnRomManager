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
public sealed class ReviewResolver(JobLock jobLock, IIngestReviewRepository reviews, IGameRepository games,
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

    private async Task<ReviewResolutionResult> ActAsync(long id, ReviewResolutionRequest? request, AuditActor actor, CancellationToken ct)
    {
        var userId = actor.UserId!.Value; // review endpoints always have a signed-in user
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new("JobRunning");
        var item = await reviews.GetAsync(id, ct).ConfigureAwait(false);
        if (item is not { Status: "Pending" })
            return new("NotFound");
        var source = SafePath.Resolve(paths.Value.Ingest, Path.GetRelativePath(paths.Value.Ingest, item.ArchivePath ?? item.SourcePath));
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
                await reviews.SetResolutionAsync(id, "Dismissed", null, userId, time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                return new();
            }

            var system = systems.ById(item.SystemId)!;
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
                system = chosen;
                var store = dats.For(system.Id);
                var datRom = store?.Roms.FirstOrDefault(r => r.GameName == request.DatGameName);
                if (datRom is null || store is null)
                    return ReviewResolutionResult.Invalid("datGameName", "Choose a game in the loaded DAT.");
                var parsed = NoIntroName.Parse(Path.GetFileNameWithoutExtension(datRom.RomName));
                title = parsed.BaseTitle; regions = parsed.Regions; languages = parsed.Languages; revision = parsed.Revision;
                flags = [.. parsed.Flags, "Unverified"];
                filename = Path.GetFileNameWithoutExtension(datRom.RomName) + " (Unverified)" + system.RomExtensions[0];
                identification = new(IdentificationMethod.Manual, store.DatName, store.Version, datRom.GameName);
                metadata = store.MetadataFor(datRom.Crc32);
            }
            else if (request.Action == "Attach" && request.Flag == "None")
            {
                if (item.Reason != "AmbiguousGroup" ||
                    !(JsonSerializer.Deserialize<Guid[]>(item.CandidateGameIds ?? "[]") ?? []).Contains(request.TargetGameId!.Value))
                    return ReviewResolutionResult.Invalid("targetGameId", "Choose one of the ambiguous item's candidates.");
                var dat = JsonNode.Parse(item.DatIdentification!)!;
                // Keep the queued identity even when the loaded DATs have changed.
                identification = new(IdentificationMethod.Dat, dat["datName"]!.GetValue<string>(), dat["datVersion"]?.GetValue<string>(), dat["datGameName"]!.GetValue<string>());
                filename = Path.GetFileNameWithoutExtension(dat["romName"]!.GetValue<string>()) + system.RomExtensions[0];
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
                filename = FileNameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(item.OriginalFileName)) + $" ({request.Flag})" + system.RomExtensions[0];
            }
            else
            {
                title = request.Title!.Trim();
                regions = request.Regions!; languages = request.Languages!; flags = request.Flags!; revision = request.Revision;
                var stem = title + (regions.Count == 0 ? "" : $" ({string.Join(", ", regions)})") +
                    (string.IsNullOrEmpty(revision) ? "" : $" ({revision})") + string.Concat(flags.Select(f => $" ({f})"));
                filename = FileNameSanitizer.Sanitize(stem) + system.RomExtensions[0];
            }

            if (filename.Contains("..", StringComparison.Ordinal))
                return ReviewResolutionResult.Invalid("title", "The generated ROM filename must not contain '..'; use Manual with another title.");
            if (target is { } targetId)
            {
                var game = await games.GetAsync(targetId, ct).ConfigureAwait(false);
                if (game is null)
                    return new("NotFound");
                if (game.SystemId != system.Id)
                    return ReviewResolutionResult.Invalid("targetGameId", "Target must belong to the same system.");
            }
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
            if (Directory.Exists(work))
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
