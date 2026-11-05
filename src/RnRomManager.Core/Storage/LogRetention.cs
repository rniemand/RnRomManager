using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Core.Storage;

/// <summary>
/// Spec §13 retention: deletes <c>rnrommanager-yyyy-MM-dd.log</c> files in <c>Paths:Logs</c> whose UTC day is more than
/// <c>Logging:RetentionDays</c> days before today (one file per UTC day, so the name carries the age). A file whose
/// name has no date falls back to its last write time. Run at startup and daily at 00:05 UTC by the scheduler.
/// </summary>
public sealed class LogRetention(IOptions<PathsOptions> paths, IOptions<LoggingOptions> logging, TimeProvider time, ILogger<LogRetention> logger)
{
    public const string FilePattern = "rnrommanager-*.log";
    private const string Prefix = "rnrommanager-";

    /// <returns>How many files were deleted.</returns>
    public int Purge()
    {
        var folder = paths.Value.Logs;
        if (!Directory.Exists(folder))
            return 0;
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var oldestKept = today.AddDays(-logging.Value.RetentionDays);
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(folder, FilePattern))
        {
            var stem = Path.GetFileNameWithoutExtension(file)[Prefix.Length..];
            var day = DateOnly.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : DateOnly.FromDateTime(File.GetLastWriteTimeUtc(file));
            if (day >= oldestKept)
                continue;
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Couldn't delete old log {File}: {Error}", file, ex.Message);
            }
        }
        if (deleted > 0)
            logger.LogInformation("Deleted {Count} log files older than {Days} days", deleted, logging.Value.RetentionDays);
        return deleted;
    }
}
