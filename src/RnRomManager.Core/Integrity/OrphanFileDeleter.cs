using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Integrity;

/// <summary>
/// IN3: deletes the file an open <c>OrphanFile</c> issue names, under the global job lock (as <c>IngestFileDeleter</c>),
/// then resolves the issue. Only a regular file reached without links is deleted, and only one that existed is audited.
/// </summary>
public sealed class OrphanFileDeleter(
    JobLock jobLock,
    IIntegrityIssueRepository issues,
    IGameRepository games,
    IRomRepository roms,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    TimeProvider time,
    ILogger<OrphanFileDeleter> logger)
{
    public async Task<OrphanDeleteOutcome> DeleteAsync(long issueId, AuditActor actor, CancellationToken ct)
    {
        var issue = await issues.GetAsync(issueId, ct).ConfigureAwait(false);
        if (issue is null)
            return OrphanDeleteOutcome.NotFound;
        if (issue is not { Type: IntegrityIssueType.OrphanFile, Status: IntegrityIssue.Open, ZipPath: { } zipPath })
            return OrphanDeleteOutcome.NotOpenOrphan;
        using var held = jobLock.TryAcquire("Deleting orphan file");
        if (held is null)
            return OrphanDeleteOutcome.JobRunning;
        // Re-checked under the lock: an ingest since the check may have made the path part of the library.
        if (await games.ZipPathExistsAsync(zipPath, ct).ConfigureAwait(false)
            || (await roms.ListExternalAsync(ct).ConfigureAwait(false))
                .Any(r => string.Equals(ExternalIntegrityCheck.FileKey(r), zipPath, StringComparison.OrdinalIgnoreCase)))
            return OrphanDeleteOutcome.NoLongerOrphan;

        var root = paths.Value.Roms;
        ExternalPathCheck check;
        try
        {
            check = ExternalRomPath.ClassifyFile(root, SafePath.Resolve(root, zipPath));
        }
        catch (UnsafePathException)
        {
            return OrphanDeleteOutcome.NotDeletable;
        }
        if (check.Status == ExternalPathStatus.Found)
        {
            try
            {
                File.Delete(check.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Deleting orphan file '{Path}' failed", check.Path);
                return OrphanDeleteOutcome.DeleteFailed;
            }
            audit.Record(new AuditEvent(AuditActions.OrphanFileDeleted, actor, issue.SystemId, Path: AuditPaths.Library(zipPath)));
        }
        else if (check.Status != ExternalPathStatus.Missing)
        {
            logger.LogWarning("Orphan file '{Path}' was not deleted: {Details}", zipPath, check.Details);
            return OrphanDeleteOutcome.NotDeletable;
        }
        // The file is gone either way, so the resolve isn't cancelled with the request.
        await issues.ResolveAsync([issueId], time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("User {UserId} deleted orphan file '{Path}'", actor.UserId, zipPath);
        return OrphanDeleteOutcome.Deleted;
    }
}
