using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Library;

/// <summary>
/// Spec §10.2 "Retry media" for one game: when its box art is the default or missing, or it has no snap, re-run the
/// thumbnail lookup (primary release's <c>datGameName</c> first, then the others in rank order). A real box art replaces
/// the default (a missing one gets at least the default), a found snap is added, and the game is rewritten (atomic ZIP +
/// DB, §6.1/§14.2) only when that changes something. The caller holds the job lock.
/// </summary>
public sealed class GameMediaRetry(ILibraryMirror mirror, InfoJsonReader reader, ISystemRegistry systems, MediaService media,
    IOptions<PathsOptions> paths, TimeProvider time, ILogger<GameMediaRetry> logger)
{
    /// <param name="skipManualOnly">The <c>MediaRetry</c> job skips games where every release is <c>Manual</c> (D38).</param>
    public async Task<MediaRetryResult> RetryAsync(GameRow row, CircuitBreaker breaker, bool skipManualOnly, CancellationToken ct)
    {
        var target = LibraryTarget.Open(paths.Value.Roms, row.ZipPath, reader);
        if (target.Code is not null)
            return new(MediaRetryOutcome.Error, target.Code, target.Details);

        using var existing = target.Zip!;
        var current = target.Game!;
        var boxArt = current.Images.FirstOrDefault(i => i.Type == ImageType.BoxArt);
        var hasSnap = current.Images.Any(i => i.Type == ImageType.Snap);
        if (boxArt is { IsDefault: false } && hasSnap)
            return new(MediaRetryOutcome.Skipped);
        if (skipManualOnly && current.Roms.All(r => r.Identification.Method == IdentificationMethod.Manual))
            return new(MediaRetryOutcome.Skipped);
        if (systems.ById(current.System) is not { } handler)
            return new(MediaRetryOutcome.Error, LibraryTarget.TargetZipUnreadable, $"{row.ZipPath}: system '{current.System}' isn't registered");

        var names = current.Roms.OrderBy(PrimaryRelease.Rank, PrimaryReleaseRanker.Instance).Select(r => r.Identification.DatGameName);
        var found = await media.FindAsync(handler, names, breaker, ct).ConfigureAwait(false);
        var newBoxArt = boxArt is null || (boxArt.IsDefault && !found.BoxArt.IsDefault) ? found.BoxArt : null;
        var newSnap = hasSnap ? null : found.Snap;
        if (newBoxArt is null && newSnap is null)
            return new(MediaRetryOutcome.NotFound);

        var now = time.GetUtcNow();
        var game = current with
        {
            Images = [.. current.Images.Where(i => !(newBoxArt is not null && i.Type == ImageType.BoxArt)),
                .. new[] { newBoxArt, newSnap }.OfType<MediaImage>().Select(i => i.ToInfoJson(now))],
            Metadata = current.Metadata with { UpdatedAt = now, AppVersion = AppInfo.Version },
        };
        var entries = new[] { newBoxArt, newSnap }.OfType<MediaImage>()
            .ToDictionary(i => LibraryZip.ImageEntry(i.FileName), i => (Func<Stream>)(() => new MemoryStream(i.Bytes)));

        // Not cancellable once the write starts: a ZIP is never left half-applied.
        var path = SafePath.Resolve(paths.Value.Roms, row.ZipPath);
        try
        {
            await new LibraryZipWriter(reader).WriteAsync(path, existing, new LibraryZipChanges(game, entries)).ConfigureAwait(false);
        }
        catch (LibraryZipVerifyException ex)
        {
            return new(MediaRetryOutcome.Error, LibraryEditResult.VerifyFailed, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(MediaRetryOutcome.Error, LibraryEditResult.IoError, ex.Message);
        }

        var info = new FileInfo(path);
        try
        {
            await mirror.ReplaceAsync(new MirrorEntry(game, row.ZipPath, info.Length, info.LastWriteTimeUtc), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new(MediaRetryOutcome.Error, LibraryEditResult.DbWriteFailed, ex.Message);
        }
        logger.LogInformation("Media for game {GameId} updated ({ZipPath}): box art {BoxArt}, snap {Snap}",
            row.Id, row.ZipPath, newBoxArt is null ? "kept" : newBoxArt.IsDefault ? "default" : "found", newSnap is null ? "kept" : "found");
        return new(MediaRetryOutcome.Updated);
    }
}
