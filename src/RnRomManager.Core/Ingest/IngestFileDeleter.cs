using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>D2: one file to delete; <see cref="Folder"/> is <c>pending</c> (<c>ingest/&lt;system&gt;/</c>) or <c>failed</c> (its <c>_failed/</c>).</summary>
public sealed record IngestDeleteRef(string? System, string? Folder, string? Name);

/// <param name="Code"><c>JobRunning</c> when the lock was busy (nothing deleted), else null.</param>
public sealed record IngestDeleteResult(int Deleted, IReadOnlyList<IngestDeleteRef> Failed, string? Code = null);

/// <summary>D2: hard-deletes pending files and failed files (with their <c>.error.txt</c>) under the global job lock.</summary>
public sealed class IngestFileDeleter(JobLock jobLock, IIngestReviewRepository reviews, ISystemRegistry systems, IOptions<PathsOptions> paths,
    IAuditLog audit, AuditPaths auditPaths, ILogger<IngestFileDeleter> logger)
{
    public const string Pending = "pending";
    public const string Failed = "failed";
    public const int MaxFiles = 500;

    /// <returns>Field errors under <c>files</c>; empty when the request is valid.</returns>
    public static Dictionary<string, string[]> Validate(IReadOnlyList<IngestDeleteRef?>? files, ISystemRegistry systems, string ingestRoot)
    {
        if (files is null || files.Count == 0)
            return Error("Select at least one file.");
        if (files.Count > MaxFiles)
            return Error($"At most {MaxFiles} files per request.");
        var errors = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var problem = Problem(files[i], systems, ingestRoot);
            if (problem is not null)
                errors.Add($"files[{i}]: {problem}");
        }
        return errors.Count == 0 ? [] : new() { ["files"] = [.. errors] };

        static Dictionary<string, string[]> Error(string message) => new() { ["files"] = [message] };
    }

    private static string? Problem(IngestDeleteRef? file, ISystemRegistry systems, string ingestRoot)
    {
        if (file?.System is not { } system || systems.ById(system) is null)
            return $"unknown system '{file?.System}'.";
        if (file.Folder is not (Pending or Failed))
            return $"folder must be '{Pending}' or '{Failed}'.";
        if (file.Name is not { Length: > 0 } name || name is "." or ".." || name.Contains('/') || name.Contains('\\')
            || IngestFileScanner.IsIgnored(name)
            || (file.Folder == Failed && name.EndsWith(IngestFileScanner.ErrorSuffix, StringComparison.OrdinalIgnoreCase)))
            return $"invalid file name '{file.Name}'.";
        try
        {
            PathOf(ingestRoot, file);
            return null;
        }
        catch (UnsafePathException)
        {
            return $"invalid file name '{file.Name}'.";
        }
    }

    /// <exception cref="UnsafePathException">The entry resolves outside its folder (not possible after <see cref="Validate"/>).</exception>
    private static string PathOf(string ingestRoot, IngestDeleteRef file) =>
        SafePath.Resolve(ingestRoot, file.Folder == Failed
            ? Path.Combine(file.System!, StorageFolders.FailedFolder, file.Name!)
            : Path.Combine(file.System!, file.Name!));

    /// <param name="files">Already checked by <see cref="Validate"/>.</param>
    public async Task<IngestDeleteResult> DeleteAsync(IReadOnlyList<IngestDeleteRef> files, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire();
        if (held is null)
            return new(0, [], "JobRunning");
        var seen = new HashSet<IngestDeleteRef>();
        var failed = new List<IngestDeleteRef>();
        var deleted = 0;
        foreach (var file in files)
        {
            if (!seen.Add(file))
                continue;
            if (await DeleteOneAsync(file, actor, ct).ConfigureAwait(false))
                deleted++;
            else
                failed.Add(file);
        }
        logger.LogInformation("User {UserId} deleted {Deleted} ingest file(s), {Failed} failed", actor.UserId, deleted, failed.Count);
        return new(deleted, failed);
    }

    private async Task<bool> DeleteOneAsync(IngestDeleteRef file, AuditActor actor, CancellationToken ct)
    {
        var path = PathOf(paths.Value.Ingest, file);
        var external = systems.ById(file.System!) is { EmbedRomsInZip: false };
        if (external)
        {
            // G4: only a regular file reached without a link is deleted; a confirmed-absent one is handled as gone.
            var check = ExternalRomPath.ClassifyFile(paths.Value.Ingest, path);
            if (check.Status is not (ExternalPathStatus.Found or ExternalPathStatus.Missing))
            {
                logger.LogWarning("Ingest file '{Path}' was not deleted: {Details}", path, check.Details);
                return false;
            }
            path = check.Path;
        }
        // File.Delete succeeds on a missing file, so only a file that existed gets an audit row.
        var existed = File.Exists(path);
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Gone along with its folder: the goal is reached.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Deleting ingest file '{Path}' failed", path);
            return false;
        }
        if (existed)
            audit.Record(new AuditEvent(AuditActions.IngestFileDeleted, actor, file.System, Path: auditPaths.Of(path), Details: file.Folder));
        if (file.Folder == Failed)
        {
            try
            {
                var diagnostic = path + IngestFileScanner.ErrorSuffix;
                // An unsafe diagnostic of an external system stays where it is.
                if (!external || ExternalRomPath.ClassifyFile(paths.Value.Ingest, diagnostic).Status == ExternalPathStatus.Found)
                    File.Delete(diagnostic);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("{Path}: couldn't delete the .error.txt: {Error}", path, ex.Message);
            }
        }
        else
        {
            // External: the file is gone, so its rows are removed even when the request was abandoned meanwhile.
            await reviews.DeletePendingBySourceAsync(path, ArchiveExtractor.IsArchive(file.Name!) ? path : null,
                external ? CancellationToken.None : ct).ConfigureAwait(false);
        }
        return true;
    }
}
