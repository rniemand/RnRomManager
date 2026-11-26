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

/// <summary>Hard delete of a review item's source (D2–D4, Q4, I4) for <see cref="ReviewResolver"/>.</summary>
internal sealed class ReviewSourceDeleter(
    JobLock jobLock,
    IIngestReviewRepository reviews,
    ISystemRegistry systems,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    AuditPaths auditPaths,
    ILogger logger)
{
    internal async Task<ReviewResolutionResult> DeleteSourceAsync(long id, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Resolving review items");
        if (held is null)
            return new("JobRunning");
        return await DeleteHeldAsync(id, actor, ct).ConfigureAwait(false);
    }

    internal async Task<ReviewBatchDeleteResult?> DeleteSourcesAsync(IReadOnlyList<long> ids, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Resolving review items");
        if (held is null)
            return null;
        int deleted = 0, skipped = 0;
        var failed = new List<ReviewBatchDeleteFailure>();
        foreach (var id in ids)
        {
            var result = await DeleteHeldAsync(id, actor, ct).ConfigureAwait(false);
            if (result.Code is null) deleted++;
            else if (result.Code == "NotFound") skipped++;
            else failed.Add(new(id, result.Code));
        }
        return new(deleted, skipped, failed);
    }

    private async Task<ReviewResolutionResult> DeleteHeldAsync(long id, AuditActor actor, CancellationToken ct)
    {
        var item = await reviews.GetAsync(id, ct).ConfigureAwait(false);
        if (item is not { Status: "Pending" })
            return new("NotFound");
        if (ReviewResolver.IsExternal(item, systems))
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
}
