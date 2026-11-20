using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Library;

/// <summary>
/// Release editing B19: the one-off move of every game's version 1 saves into per-user folders (B16, B18), as
/// <see cref="ZipRepackJob"/> does its rewrite: one item per game, a checked write and the mirror per moved game, cancel
/// between games. Sets <see cref="MigratedSetting"/> when it ends without being cancelled.
/// </summary>
public sealed class SaveFoldersJob(IGameRepository games, ILibraryMirror mirror, InfoJsonReader reader, ISystemRegistry systems,
    IJobItemRepository items, IOptions<PathsOptions> paths, IAuditLog audit, IFreeSpace freeSpace, TimeProvider time,
    IRomStorageSettings settings, ISettingsRepository appSettings, ILogger<SaveFoldersJob> logger) : IJob
{
    public const string Moved = "Moved";
    public const string Skipped = "Skipped";
    public const string Error = "Error";

    /// <summary>The <c>app_settings</c> name the starter checks: <c>"true"</c> once the job has run.</summary>
    public const string MigratedSetting = "saves.folders.migrated";

    public JobType Type => JobType.SaveFolders;
    public JobMode? Mode => null;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        // Every game: games.save_count may be stale, and only the index tells the version.
        var rows = (await games.ListAllAsync(cancellationToken).ConfigureAwait(false)).OrderBy(g => g.ZipPath, StringComparer.Ordinal).ToList();
        context.Progress.SetTotal(rows.Count);
        int moved = 0, skipped = 0, error = 0;

        foreach (var row in rows)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            context.Progress.SetCurrentItem(row.ZipPath);
            (string Outcome, string? Code, string? Details) result;
            try
            {
                // Not cancellable once a game starts: a ZIP is never half-written.
                result = await MoveAsync(row).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Moving the saves of {ZipPath} failed", row.ZipPath);
                result = (Error, "Unexpected", ex.Message);
            }

            switch (result.Outcome)
            {
                case Moved:
                    moved++;
                    audit.Record(new AuditEvent(AuditActions.SaveUpdated, context.Actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                        Details: "Moved saves to user folders"));
                    break;
                case Skipped:
                    skipped++;
                    break;
                default:
                    error++;
                    logger.LogError("Moving the saves of {ZipPath} failed: {Code} {Details}", row.ZipPath, result.Code, result.Details);
                    break;
            }
            await items.InsertAsync(new JobItem
            {
                JobId = context.JobId,
                FileId = row.Id,
                GameId = row.Id,
                SystemId = row.SystemId,
                SourcePath = AuditPaths.Library(row.ZipPath),
                Outcome = result.Outcome,
                Reason = result.Code,
                Details = result.Details,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            }, CancellationToken.None).ConfigureAwait(false);
            context.Progress.ItemDone();
        }

        // Errors included: those ZIPs need an Admin's hand, and their next save write upgrades them anyway.
        if (!cancellationToken.IsCancellationRequested)
            await appSettings.SetAsync(new Dictionary<string, string> { [MigratedSetting] = "true" }, CancellationToken.None).ConfigureAwait(false);
        return new { moved, skipped, error };
    }

    private async Task<(string Outcome, string? Code, string? Details)> MoveAsync(GameRow row)
    {
        var library = paths.Value.Roms;
        var target = LibraryTarget.OpenChecked(library, row.ZipPath, reader, row.Id, row.SystemId);
        if (target.Code is not null)
        {
            target.Zip?.Dispose();
            return (Error, "ZipUnreadable", target.Details);
        }
        using var existing = target.Zip!;
        var game = target.Game!;
        var read = GameSaves.Read(existing);
        if (read.State == SavesState.Unreadable)
            return (Error, LibraryEditResult.SavesUnreadable, read.Problem);
        if (read.State == SavesState.Absent || read.Index!.Version != 1)
            return (Skipped, null, null);
        if (systems.ById(row.SystemId) is { } handler && RomStorage.IsPending(handler, game))
            return (Error, LibraryEditResult.StorageMigrationPending, $"{row.ZipPath} waits for the storage job");

        var needed = existing.EntryNames.Sum(n => existing.EntryLength(n) ?? 0) + new FileInfo(existing.Path).Length
            + StorageMigrationJob.SpaceMarginBytes;
        if (freeSpace.AvailableBytes(existing.Path) is { } free && free < needed)
            return (Error, "InsufficientSpace", $"{needed} bytes needed at {existing.Path}, {free} free");

        var added = new Dictionary<string, Func<Stream>>();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var saves = GameSaves.Upgrade(read.Index.Saves, s => () => existing.OpenEntry(s.Entry, s.SizeBytes), added, removed);
        if (GameSaves.Serialize(saves) is not { } index)
            return (Error, LibraryEditResult.SavesIndexFull, $"{row.ZipPath}: the version 2 saves.json would be over 1 MiB");

        ExternalFileSnapshot published;
        try
        {
            // B19: info.json as it is, so no Touched().
            published = await new LibraryZipWriter(reader, settings.Compression(row.SystemId)).WriteCheckedAsync(library, existing.Path, existing,
                new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>(), Saves: new SavesChange(index, added, removed))).ConfigureAwait(false);
        }
        catch (LibraryZipVerifyException ex)
        {
            return (Error, "VerifyFailed", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Error, "IoError", ex.Message);
        }

        try
        {
            await mirror.ReplaceAsync(new MirrorEntry(game, row.ZipPath, published.Length, published.LastWriteTimeUtc), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            return (Error, "DbWriteFailed", ex.Message);
        }
        logger.LogInformation("Saves of {ZipPath} moved to user folders", row.ZipPath);
        return (Moved, null, null);
    }
}
