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

/// <summary>I1–I3: checks and hashes an external system's source streamed, for <see cref="IngestPipeline"/>.</summary>
internal sealed class ExternalIngest(
    IngestPipeline pipeline,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger<IngestPipeline> logger)
{
    /// <summary>
    /// I1–I3 for an external system: the file is opened through a checked handle and hashed in one streamed read,
    /// never held in memory. A loose file that differs from the ingest listing, is under 10 s old or changes while
    /// it is hashed is <c>Skipped</c> <c>TooRecent</c> and left where it is.
    /// </summary>
    internal async Task<IngestItemResult?> ProcessExternalAsync(
        IngestFile file,
        ISystemHandler folder,
        IReadOnlyList<ISystemHandler> family,
        ISystemHandler byExtension,
        Guid jobId,
        AuditActor actor,
        CircuitBreaker breaker,
        CancellationToken cancellationToken,
        JobProgress? progress)
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
            return pipeline.IngestFileMoves.FailFile(file, actor, IngestFailures.TooSmall, $"{size} bytes; {byExtension.Id} ROMs are at least {byExtension.MinRomSizeBytes}", progress: progress);
        if (size > byExtension.MaxRomSizeBytes)
            return pipeline.IngestFileMoves.FailFile(file, actor, IngestFailures.TooLarge, $"{size} bytes; {byExtension.Id} ROMs are at most {byExtension.MaxRomSizeBytes}", progress: progress);

        var (outcome, hashed, failure) = await HashCheckedAsync(file, root, fresh.Path, snapshot, byExtension.MaxRomSizeBytes, loose, cancellationToken,
            progress).ConfigureAwait(false);
        if (outcome == HashOutcome.Failed)
            return IngestItemResult.Error(IngestErrors.IoError, failure, file.SystemId);
        if (outcome == HashOutcome.Changed)
            return tooRecent; // still being copied in
        logger.LogDebug("{Path}: {Size} bytes streamed, crc32 {Crc32}, sha1 {Sha1}", file.FullPath, size, hashed!.Crc32, hashed.Sha1);

        // I3: only handlers that can store this extension and size identify it.
        var extension = Path.GetExtension(file.Name);
        var eligible = family.Where(h => h.RomExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            && size >= h.MinRomSizeBytes && size <= h.MaxRomSizeBytes).ToList();

        // Arcade I2: a set ZIP's file list, from a second checked open of the file that was hashed.
        IReadOnlyList<MameEntry>? mameEntries = null;
        if (eligible.Any(h => h.DatSource.IsRomset()))
        {
            var opened = ExternalRomPath.OpenRead(root, fresh.Path, FileShare.ReadWrite | FileShare.Delete, snapshot);
            if (opened.Status == ExternalPathStatus.Changed)
                return tooRecent;
            if (opened.Status != ExternalPathStatus.Found)
                return IngestItemResult.Error(IngestErrors.IoError, opened.Details, file.SystemId);
            string? zipError;
            try
            {
                using var stream = opened.Stream!;
                (mameEntries, zipError) = Path.GetExtension(file.Name).ToLowerInvariant() is ".7z" or ".fbneo7z"
                    ? MameZip.ReadSevenZipEntries(stream)
                    : MameZip.ReadEntries(stream);
                if (ExternalRomPath.SnapshotOf(stream) != snapshot)
                    return tooRecent;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return IngestItemResult.Error(IngestErrors.IoError, ex.Message, file.SystemId);
            }
            if (zipError is not null)
                return pipeline.IngestFileMoves.FailFile(file, actor, IngestFailures.InvalidHeader, zipError, hashed.Sha1, progress);
        }

        return await pipeline.IngestIdentifier.IdentifyAsync(file, folder, eligible, new RomHashes(hashed.Crc32, hashed.Md5, hashed.Sha1), size, hashed.Header, null,
            new ExternalSource(root, fresh.Path, snapshot, progress), jobId, actor, breaker, cancellationToken, mameEntries).ConfigureAwait(false);
    }

    /// <summary>
    /// I2/T9: one streamed read (CRC32, MD5, SHA1 and the header bytes) through a handle that must open
    /// <paramref name="snapshot"/>'s file, which must still be unchanged once it is read.
    /// </summary>
    /// <param name="recheckPath">Also require <paramref name="path"/> to still name that file afterwards.</param>
    /// <param name="progress">Gets a <c>Hashing</c> step for the read (spec B2/B3).</param>
    internal async Task<(HashOutcome Outcome, RomFileHashes? Hashes, string? Details)> HashCheckedAsync(IngestFile file, string root, string path,
        ExternalFileSnapshot snapshot, long maxBytes, bool recheckPath, CancellationToken cancellationToken, JobProgress? progress = null)
    {
        var opened = ExternalRomPath.OpenRead(root, path, FileShare.ReadWrite | FileShare.Delete, snapshot);
        if (opened.Status == ExternalPathStatus.Changed)
            return (HashOutcome.Changed, null, opened.Details);
        if (opened.Status != ExternalPathStatus.Found)
            return (HashOutcome.Failed, null, opened.Details);
        using var stream = opened.Stream!;
        progress?.BeginStep(JobStepPhases.Hashing, snapshot.Length, time.GetUtcNow().UtcDateTime);
        RomFileHashes? hashed;
        try
        {
            pipeline.ExternalHooks?.BeforeHash?.Invoke(file);
            var input = pipeline.ExternalHooks?.WrapHashStream?.Invoke(stream) ?? stream;
            hashed = await RomFileHasher.HashAsync(input, maxBytes, cancellationToken, progress is null ? null : progress.StepAdvanced).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (HashOutcome.Failed, null, ex.Message);
        }
        finally
        {
            progress?.EndStep();
        }
        if (hashed is null || hashed.SizeBytes != snapshot.Length || ExternalRomPath.SnapshotOf(stream) != snapshot
            || (recheckPath && ExternalRomPath.ClassifyFile(root, path).Snapshot != snapshot))
            return (HashOutcome.Changed, null, $"{path} changed while it was read");
        return (HashOutcome.Hashed, hashed, null);
    }
}
