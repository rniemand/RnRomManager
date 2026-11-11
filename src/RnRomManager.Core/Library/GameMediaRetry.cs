using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Library;

/// <summary>
/// Spec §10.2 "Retry media" for one game: when it still has the placeholder, no images, or no snap, re-run the
/// thumbnail lookup (primary release's <c>datGameName</c> first, then the others in rank order) and apply it per I4
/// (<see cref="GameImages.ApplyFetched"/>): a real box art replaces the placeholder, a game with no images gets at least
/// the placeholder, a found snap is added. The game is rewritten (atomic ZIP + DB, §6.1/§14.2) only when that changes
/// something. The caller holds the job lock.
/// </summary>
public sealed class GameMediaRetry(ILibraryMirror mirror, InfoJsonReader reader, ISystemRegistry systems, MediaService media,
    IOptions<PathsOptions> paths, IAuditLog audit, TimeProvider time, ILogger<GameMediaRetry> logger)
{
    /// <param name="skipManualOnly">The <c>MediaRetry</c> job skips games where every release is <c>Manual</c> (D38).</param>
    /// <param name="actor">AU4: <c>MediaFetched</c> is recorded once the ZIP is written (also when the DB update then fails).</param>
    public async Task<MediaRetryResult> RetryAsync(GameRow row, CircuitBreaker breaker, bool skipManualOnly, AuditActor actor, CancellationToken ct)
    {
        var target = LibraryTarget.OpenForWrite(paths.Value.Roms, row.ZipPath, reader, systems, row.Id, row.SystemId);
        if (target.Code is not null)
            return new(MediaRetryOutcome.Error, target.Code, target.Details);

        using var existing = target.Zip!;
        var current = target.Game!;
        var hasSnap = current.Images.Any(i => i.Type == ImageType.Snap);
        if (current.Images.Count > 0 && !current.Images.Any(i => i.IsDefault) && hasSnap)
            return new(MediaRetryOutcome.Skipped);
        if (skipManualOnly && current.Roms.All(r => r.Identification.Method == IdentificationMethod.Manual))
            return new(MediaRetryOutcome.Skipped);
        if (systems.ById(current.System) is not { } handler)
            return new(MediaRetryOutcome.Error, LibraryTarget.TargetZipUnreadable, $"{row.ZipPath}: system '{current.System}' isn't registered");

        var names = PrimaryRelease.Order(current.Roms, handler.DatSource).Select(r => r.Identification.DatGameName);
        var found = await media.FindAsync(handler, names, breaker, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (GameImages.ApplyFetched(current.Images, found, now) is not var (images, imageEntries))
            return new(MediaRetryOutcome.NotFound);

        var game = current with
        {
            Images = images,
            Metadata = current.Metadata with { UpdatedAt = now, AppVersion = AppInfo.Version },
        };
        var entries = imageEntries.ToDictionary(e => LibraryZip.ImageEntry(e.Key), e => (Func<Stream>)(() => new MemoryStream(e.Value)));

        // Not cancellable once the write starts: a ZIP is never left half-applied.
        var path = SafePath.Resolve(paths.Value.Roms, row.ZipPath);
        try
        {
            var changes = new LibraryZipChanges(game, entries);
            // G4: a checked ZIP is replaced only through the checked write.
            if (LibraryTarget.NeedsCheck(systems, row.ZipPath, game.System))
                await new LibraryZipWriter(reader).WriteCheckedAsync(paths.Value.Roms, path, existing, changes).ConfigureAwait(false);
            else
                await new LibraryZipWriter(reader).WriteAsync(path, existing, changes).ConfigureAwait(false);
        }
        catch (LibraryZipVerifyException ex)
        {
            return new(MediaRetryOutcome.Error, LibraryEditResult.VerifyFailed, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(MediaRetryOutcome.Error, LibraryEditResult.IoError, ex.Message);
        }
        audit.Record(new AuditEvent(AuditActions.MediaFetched, actor, current.System, row.Id, AuditPaths.Library(row.ZipPath),
            Details: string.Join(", ", imageEntries.Keys)));

        var info = new FileInfo(path);
        try
        {
            await mirror.ReplaceAsync(new MirrorEntry(game, row.ZipPath, info.Length, info.LastWriteTimeUtc), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new(MediaRetryOutcome.Error, LibraryEditResult.DbWriteFailed, ex.Message);
        }
        logger.LogInformation("Media for game {GameId} updated ({ZipPath}): {Images} written", row.Id, row.ZipPath, string.Join(", ", imageEntries.Keys));
        return new(MediaRetryOutcome.Updated);
    }
}
