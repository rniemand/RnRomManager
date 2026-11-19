using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Storage;

/// <summary>
/// ZIP Compression Z9: applies an Admin's level change (if any) under the job lock, then rewrites every game ZIP of one
/// compression-configurable system at that system's level. Each game is re-opened, checked for free space, rewritten
/// through the checked write (tmp, verify, atomic move) and mirrored. One job item per game; cancel and shutdown stop
/// between games.
/// </summary>
public sealed class ZipRepackJob(IGameRepository games, ILibraryMirror mirror, InfoJsonReader reader, ISystemRegistry systems,
    IJobItemRepository items, IOptions<PathsOptions> paths, IAuditLog audit, IFreeSpace freeSpace, TimeProvider time,
    IRomStorageSettings settings, ILogger<ZipRepackJob> logger) : IJob
{
    public const string Repacked = "Repacked";
    public const string Error = "Error";

    public JobType Type => JobType.ZipRepack;
    public JobMode? Mode => null;

    /// <summary>The system whose ZIPs are repacked.</summary>
    public string SystemId { get; set; } = "";

    /// <summary>The level to save first; null for Repack again.</summary>
    public ZipCompression? Level { get; set; }

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        // Step 0: a storage move may have finished since the endpoint checked.
        if (systems.ById(SystemId) is not { EmbedRomsInZip: true } handler)
        {
            logger.LogWarning("ZIP repack of {SystemId} skipped: its ROMs aren't stored in the ZIP any more", SystemId);
            return new { repacked = 0, error = 0 };
        }

        // Step 1: a failed save fails the job before any repack; the same value (a race) saves and audits nothing.
        var current = settings.Compression(handler.Id);
        if (Level is { } level && level != current)
        {
            await settings.SetCompressionAsync(handler.Id, level, cancellationToken).ConfigureAwait(false);
            audit.Record(new AuditEvent(AuditActions.ZipCompressionChanged, context.Actor, handler.Id, Details: $"{current} → {level}"));
            logger.LogInformation("ZIP compression of {SystemId} set to {Level}", handler.Id, level);
        }

        var rows = (await games.ListAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(g => string.Equals(g.SystemId, handler.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.ZipPath, StringComparer.Ordinal)
            .ToList();
        context.Progress.SetTotal(rows.Count);
        int repacked = 0, error = 0;

        foreach (var row in rows)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            context.Progress.SetCurrentItem(row.ZipPath);
            (string? Code, string? Details) outcome;
            try
            {
                // Not cancellable once a game starts: a ZIP is never half-written.
                outcome = await RepackAsync(row, handler).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "ZIP repack of {ZipPath} failed", row.ZipPath);
                outcome = ("Unexpected", ex.Message);
            }

            if (outcome.Code is null)
                repacked++;
            else
            {
                error++;
                logger.LogError("ZIP repack of {ZipPath} failed: {Code} {Details}", row.ZipPath, outcome.Code, outcome.Details);
            }
            await items.InsertAsync(new JobItem
            {
                JobId = context.JobId,
                FileId = row.Id,
                GameId = row.Id,
                SystemId = row.SystemId,
                SourcePath = AuditPaths.Library(row.ZipPath),
                Outcome = outcome.Code is null ? Repacked : Error,
                Reason = outcome.Code,
                Details = outcome.Details,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            }, CancellationToken.None).ConfigureAwait(false);
            context.Progress.ItemDone();
        }
        return new { repacked, error };
    }

    private async Task<(string? Code, string? Details)> RepackAsync(GameRow row, ISystemHandler handler)
    {
        var library = paths.Value.Roms;
        var target = LibraryTarget.OpenChecked(library, row.ZipPath, reader, row.Id, row.SystemId);
        if (target.Code is not null)
        {
            target.Zip?.Dispose();
            return ("ZipUnreadable", target.Details);
        }
        using var existing = target.Zip!;
        var game = target.Game!;
        if (RomStorage.IsPending(handler, game))
            return ("StorageMigrationPending", $"{row.ZipPath} waits for its ROMs to be moved into the ZIP");

        // A Store repack can approach the uncompressed size.
        var needed = existing.EntryNames.Sum(n => existing.EntryLength(n) ?? 0) + new FileInfo(existing.Path).Length
            + StorageMigrationJob.SpaceMarginBytes;
        if (freeSpace.AvailableBytes(existing.Path) is { } free && free < needed)
            return ("InsufficientSpace", $"{needed} bytes needed at {existing.Path}, {free} free");

        ExternalFileSnapshot published;
        try
        {
            published = await new LibraryZipWriter(reader, settings.Compression(handler.Id)).WriteCheckedAsync(library, existing.Path, existing,
                new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>())).ConfigureAwait(false);
        }
        catch (LibraryZipVerifyException ex)
        {
            return ("VerifyFailed", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ("IoError", ex.Message);
        }

        try
        {
            await mirror.ReplaceAsync(new MirrorEntry(game, row.ZipPath, published.Length, published.LastWriteTimeUtc), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            return ("DbWriteFailed", ex.Message);
        }
        return (null, null);
    }
}
