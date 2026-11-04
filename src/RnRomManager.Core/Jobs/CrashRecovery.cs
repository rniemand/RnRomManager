using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Core.Jobs;

/// <summary>
/// Startup step 7 (spec §4.1, §7.4): <c>Running</c> jobs → <c>Aborted</c>, then delete <c>cache/work/*</c> and every
/// <c>*.zip.tmp</c> under <c>Paths:Roms</c>. Safe because sources are only deleted after a verified ZIP + DB commit.
/// </summary>
public sealed class CrashRecovery(IJobRepository jobs, IOptions<PathsOptions> paths, TimeProvider time, ILogger<CrashRecovery> logger)
{
    public const string WorkFolder = "work";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var id in await jobs.AbortRunningAsync(time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false))
            logger.LogWarning("Job {JobId} was still Running at startup; marked Aborted", id);

        var work = new DirectoryInfo(Path.Combine(paths.Value.Cache, WorkFolder));
        if (work.Exists)
        {
            foreach (var dir in work.EnumerateDirectories())
                dir.Delete(recursive: true);
            foreach (var file in work.EnumerateFiles())
                file.Delete();
        }

        foreach (var tmp in Directory.EnumerateFiles(paths.Value.Roms, "*.zip.tmp", SearchOption.AllDirectories))
        {
            File.Delete(tmp);
            logger.LogInformation("Deleted leftover {Path}", tmp);
        }
    }
}
