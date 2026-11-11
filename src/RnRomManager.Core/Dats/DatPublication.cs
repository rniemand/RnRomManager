using Microsoft.Extensions.Logging;
using RnRomManager.Core.Storage;

namespace RnRomManager.Core.Dats;

/// <summary>Test seams of <see cref="DatPublication"/>: the directory renames and deletes it makes.</summary>
public sealed class DatPublicationHooks
{
    public Action<string, string> Move { get; init; } = Directory.Move;
    public Action<string> Delete { get; init; } = path => Directory.Delete(path, recursive: true);
}

/// <summary>
/// Spec T2: swaps a staged DAT set in for <c>cache/dats/</c> through a fixed backup, <c>cache/dats.previous/</c>
/// (outside <c>cache/work/</c>, so crash cleanup never deletes it), and reconciles a backup an interrupted swap left
/// behind. Both paths are classified first: a linked or non-directory live or backup path is never moved, restored
/// or deleted, and never counts as evidence that a swap committed.
/// </summary>
public static class DatPublication
{
    public const string PreviousFolder = "dats.previous";

    public static string LivePath(string cacheRoot) => Path.Combine(cacheRoot, DatCache.FolderName);

    public static string PreviousPath(string cacheRoot) => Path.Combine(cacheRoot, PreviousFolder);

    /// <summary>
    /// Startup, before the DATs load: a backup alone is the old set an interrupted swap moved away, so it goes back
    /// to live; with both present the staged-to-live rename committed, so live is kept and the backup removed best
    /// effort. Failures are logged and the backup kept.
    /// </summary>
    /// <returns>False when live is unsafe: startup then loads no DATs.</returns>
    public static bool RecoverAtStartup(string cacheRoot, ILogger logger, DatPublicationHooks? hooks = null)
    {
        try
        {
            Reconcile(cacheRoot, logger, hooks ?? new());
            return true;
        }
        catch (UnsafeDatSetException ex)
        {
            logger.LogError("DAT sets left as they are: {Error}", ex.Message);
            return !ex.LiveUnsafe;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError("Restoring the previous DAT set failed; {Previous} is kept: {Error}", PreviousPath(cacheRoot), ex.Message);
            return true;
        }
    }

    /// <summary>
    /// Refresh: reconciles any earlier backup (failing if it can't), moves live to the backup and the staged set to
    /// live. If the second rename fails the old set is moved back; if that fails too, the backup is kept and logged.
    /// </summary>
    /// <exception cref="IOException">Nothing was published; the old set is live, or kept in the backup.</exception>
    public static void Swap(string cacheRoot, string staged, ILogger logger, DatPublicationHooks? hooks = null)
    {
        hooks ??= new();
        try
        {
            Reconcile(cacheRoot, logger, hooks);
        }
        catch (UnsafeDatSetException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        var live = LivePath(cacheRoot);
        var previous = PreviousPath(cacheRoot);
        var hadLive = Classify(cacheRoot, live, out _) == ExternalPathStatus.Found;
        if (hadLive)
            hooks.Move(live, previous);
        try
        {
            hooks.Move(staged, live);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (hadLive)
            {
                try
                {
                    hooks.Move(previous, live);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    logger.LogError("Publishing {Staged} failed and the old set couldn't go back to {Live}; it is kept in {Previous}: {Error}",
                        staged, live, previous, restore.Message);
                }
            }
            throw;
        }
    }

    /// <summary>After a committed swap: the backup goes, best effort, only while it is still a real directory.</summary>
    public static void RemoveBackup(string cacheRoot, ILogger logger, DatPublicationHooks? hooks = null)
    {
        var previous = PreviousPath(cacheRoot);
        try
        {
            switch (Classify(cacheRoot, previous, out var details))
            {
                case ExternalPathStatus.Found:
                    (hooks ?? new()).Delete(previous);
                    break;
                case ExternalPathStatus.Missing:
                    break;
                default:
                    logger.LogWarning("Previous DAT set {Previous} was kept: {Details}", previous, details);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Previous DAT set {Previous} could not be removed: {Error}", previous, ex.Message);
        }
    }

    private static void Reconcile(string cacheRoot, ILogger logger, DatPublicationHooks hooks)
    {
        var live = LivePath(cacheRoot);
        var previous = PreviousPath(cacheRoot);
        var liveStatus = Classify(cacheRoot, live, out var liveDetails);
        var previousStatus = Classify(cacheRoot, previous, out var previousDetails);
        if (liveStatus is not (ExternalPathStatus.Found or ExternalPathStatus.Missing))
            throw new UnsafeDatSetException($"{live} is not a usable directory: {liveDetails}", liveUnsafe: true);
        if (previousStatus is not (ExternalPathStatus.Found or ExternalPathStatus.Missing))
            throw new UnsafeDatSetException($"{previous} is not a usable directory: {previousDetails}", liveUnsafe: false);
        if (previousStatus == ExternalPathStatus.Missing)
            return;
        if (liveStatus == ExternalPathStatus.Missing)
        {
            hooks.Move(previous, live);
            logger.LogWarning("An interrupted DAT refresh left only {Previous}; it is live again", previous);
            return;
        }
        hooks.Delete(previous);
        logger.LogInformation("Removed {Previous}: the refresh that left it had already published", previous);
    }

    private static ExternalPathStatus Classify(string cacheRoot, string path, out string? details)
    {
        var check = ExternalRomPath.ClassifyDirectory(cacheRoot, path);
        details = check.Details;
        return check.Status;
    }

    private sealed class UnsafeDatSetException(string message, bool liveUnsafe) : Exception(message)
    {
        public bool LiveUnsafe { get; } = liveUnsafe;
    }
}
