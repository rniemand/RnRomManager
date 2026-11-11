using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Jobs;

/// <summary>
/// Startup step 7 (spec §4.1, §7.4): <c>Running</c> jobs → <c>Aborted</c>, then delete <c>cache/work/*</c> and every
/// <c>*.zip.tmp</c> under the registered system folders of <c>Paths:Roms</c>. Safe because sources are only deleted
/// after a verified ZIP + DB commit. Links are never followed (spec G4): a linked <c>cache/work</c>, a linked entry in
/// it and a ZIP temp in or behind a linked folder are skipped, so nothing outside the storage roots is deleted.
/// </summary>
public sealed class CrashRecovery(IJobRepository jobs, ISystemRegistry systems, IOptions<PathsOptions> paths, TimeProvider time,
    ILogger<CrashRecovery> logger)
{
    public const string WorkFolder = "work";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var id in await jobs.AbortRunningAsync(time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false))
            logger.LogWarning("Job {JobId} was still Running at startup; marked Aborted", id);

        var cache = paths.Value.Cache;
        var work = ExternalRomPath.ClassifyDirectory(cache, Path.Combine(cache, WorkFolder));
        if (work.Status == ExternalPathStatus.Found)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(work.Path))
            {
                FileSystemInfo info = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    logger.LogWarning("Skipped {Path} in the work folder: it is a link", entry);
                    continue;
                }
                if (info is DirectoryInfo directory)
                    directory.Delete(recursive: true);
                else
                    info.Delete();
            }
        }
        else if (work.Status != ExternalPathStatus.Missing)
        {
            logger.LogWarning("Skipped cleaning {Path}: {Details}", work.Path, work.Details);
        }

        var roms = paths.Value.Roms;
        foreach (var handler in systems.All)
        {
            var scan = ExternalRomPath.ScanFolder(roms, handler.Id);
            foreach (var problem in scan.Problems)
                logger.LogWarning("Skipped {Path} while removing ZIP temps: {Details}", problem.RelativePath, problem.Details);
            foreach (var tmp in scan.Files.Where(f => f.RelativePath.EndsWith(".zip.tmp", StringComparison.OrdinalIgnoreCase)))
            {
                // Only the regular file the scan saw, still at a safe path.
                if (ExternalRomPath.ClassifyFile(roms, tmp.FullPath).Snapshot != tmp.Snapshot)
                    continue;
                File.Delete(tmp.FullPath);
                logger.LogInformation("Deleted leftover {Path}", tmp.FullPath);
            }
        }
    }
}
