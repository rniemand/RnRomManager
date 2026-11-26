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

/// <summary>T9 and B14 for <see cref="ReviewResolver"/>: preparing and storing an external item, and storing a set.</summary>
internal sealed class ReviewExternalPreparation(
    IIngestReviewRepository reviews,
    IGameRepository games,
    IRomRepository roms,
    DbSync dbSync,
    IngestPipeline pipeline,
    ISystemRegistry systems,
    ArchiveExtractor extractor,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger logger)
{
    /// <summary>
    /// T9 for an external system, before any action-specific check: a loose source is used as it was classified; an
    /// archived item is re-extracted through one checked handle on its archive, whose snapshot is kept for I5.
    /// </summary>
    /// <returns>The file to resolve and its snapshot, or the failure code (the Pending row is kept).</returns>
    internal (IngestFile? File, ExternalFileSnapshot? Snapshot, string? Code) PrepareExternal(long id, ReviewItem item, IngestFile file,
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
    internal async Task<ReviewResolutionResult?> RecoverCommittedAsync(
        long id,
        ReviewItem item,
        IngestFile copy,
        ExternalFileSnapshot snapshot,
        int userId)
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
        // S3a, B5: an owner row decides by its own layout (a legacy system's embedded ROM is an embedded owner, whatever the setting).
        if (systems.ById(owner.SystemId) is not { } ownerSystem || !RomStorage.MayHoldExternalFiles(ownerSystem) || owner.ExternalFileName is null)
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
    internal async Task<ReviewResolutionResult> ResolveExternalAsync(
        long id,
        ReviewItem item,
        IngestFile file,
        ExternalFileSnapshot snapshot,
        ISystemHandler system,
        string action,
        string filename,
        string title,
        IReadOnlyList<string> regions,
        IReadOnlyList<string> languages,
        string? revision,
        IReadOnlyList<string> flags,
        InfoJsonIdentification identification,
        DatMetadata metadata,
        Guid? target,
        AuditActor actor,
        int userId,
        JobProgress? progress,
        JsonObject? header)
    {
        var now = time.GetUtcNow();
        var rom = new InfoJsonRom
        {
            FileName = filename, OriginalFileName = item.OriginalFileName, BaseTitle = title,
            Regions = regions, Languages = languages, Revision = revision, Flags = flags, IsPrimary = false,
                Version = ReleaseVersion.For(flags, item.OriginalFileName),
            Identification = identification, Header = header,
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
    internal async Task<ReviewResolutionResult> ResolveSetAsync(
        IReadOnlyList<(IngestFile File, ReviewItem Row)> set,
        ISystemHandler system,
        string action,
        string title,
        IReadOnlyList<string> regions,
        IReadOnlyList<string> languages,
        string? revision,
        IReadOnlyList<string> flags,
        InfoJsonIdentification identification,
        DatMetadata metadata,
        Guid? target,
        AuditActor actor,
        int userId,
        JobProgress? progress)
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
}
