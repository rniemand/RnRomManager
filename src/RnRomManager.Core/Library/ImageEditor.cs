using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using static RnRomManager.Core.Library.LibraryEditResult;

namespace RnRomManager.Core.Library;

/// <summary>Image edits of a game (add, delete, set cover, retry media), for <see cref="LibraryEditor"/>.</summary>
internal sealed class ImageEditor(
    LibraryEditSupport libraryEditSupport,
    JobLock jobLock,
    IGameRepository games,
    IImageRepository images,
    ILibraryMirror mirror,
    ISystemRegistry systems,
    GameMediaRetry mediaRetry,
    IAuditLog audit,
    TimeProvider time,
    ILogger<LibraryEditor> logger)
{
    /// <summary>
    /// I5: stores an uploaded PNG/JPEG as-is under <c>boxart-NN</c>/<c>snap-NN</c> (I2, extension from the magic bytes).
    /// A box art replaces the placeholder (and takes its cover status); the first image of an empty game is its cover (I3).
    /// U5: an image downloaded from a URL passes <paramref name="sourceUrl"/>; it is stored on the image and in the audit details.
    /// </summary>
    internal async Task<LibraryEditResult> AddImageAsync(
        Guid gameId,
        ImageType type,
        byte[] bytes,
        AuditActor actor,
        CancellationToken ct,
        string? sourceUrl = null)
    {
        if (type is not (ImageType.BoxArt or ImageType.Snap))
            return Invalid("type", "Choose BoxArt or Snap.");
        if (bytes.Length == 0)
            return Invalid("file", "Choose an image file.");
        if (bytes.Length > ImageFormats.MaxBytes)
            return Invalid("file", "The image must be at most 8 MiB.");
        if (ImageFormats.Detect(bytes) is null)
            return Invalid("file", "The image must be a PNG or JPEG.");
        if (ImageFormats.Read(bytes) is not var (format, width, height))
            return Invalid("file", "The image's dimensions can't be read.");

        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        if (libraryEditSupport.LegacyRefusal(row) is { } legacy)
            return legacy;
        var target = libraryEditSupport.OpenForEdit(row);
        if (target.Code is not null)
            return new(target.Code, target.Details);

        using var existing = target.Zip!;
        var current = target.Game!;
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes));
        if (current.Images.Any(i => i.Sha1 == sha1))
            return new(DuplicateImage, "The game already has this image.");

        var list = current.Images.ToList();
        // I3: a box art replaces the placeholder (taking its cover status), so its name is free again.
        var placeholder = type == ImageType.BoxArt ? list.FirstOrDefault(i => i.IsDefault) : null;
        if (placeholder is not null)
            list.Remove(placeholder);
        var image = new InfoJsonImage
        {
            FileName = GameImages.NextName(list.Select(i => i.FileName), type, ImageFormats.Extension(format)),
            Type = type,
            Source = InfoJsonImage.SourceUpload,
            SourceUrl = sourceUrl,
            IsDefault = false,
            IsCover = placeholder?.IsCover ?? false,
            SizeBytes = bytes.Length,
            Sha1 = sha1,
            Width = width,
            Height = height,
            DateAdded = time.GetUtcNow(),
        };
        list.Add(image);
        if (list.Count > GameImages.MaxImages)
            return new(TooManyImages, $"A game can have at most {GameImages.MaxImages} images.");

        var game = current with { Images = GameImages.EnsureCover(list), Metadata = libraryEditSupport.Touched(current) };
        var result = await libraryEditSupport.WriteZipAsync(existing, row.ZipPath, game,
                new() { [LibraryZip.ImageEntry(image.FileName)] = () => new MemoryStream(bytes) }).ConfigureAwait(false)
            ?? await libraryEditSupport.MirrorAsync(() => mirror.ReplaceAsync(libraryEditSupport.Mirror(row.ZipPath, game), CancellationToken.None), row.ZipPath).ConfigureAwait(false);
        if (LibraryEditSupport.FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.ImageAdded, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath),
                Details: sourceUrl is null ? image.FileName : $"{image.FileName} from {sourceUrl}"));
        if (result.Code is not null)
            return result;
        logger.LogInformation("Image {FileName} ({Type}) uploaded to game {GameId} ({ZipPath})", image.FileName, type, gameId, row.ZipPath);
        return placeholder is { IsCover: true } ? result with { ReplacedDefaultCover = true } : result;
    }

    /// <summary>
    /// I6: removes an image; the placeholder can't be (<c>DefaultImage</c>). A removed cover passes to the first remaining
    /// <c>BoxArt</c> by file name, else the first remaining image, else the system's placeholder is added back as cover (I3).
    /// </summary>
    internal async Task<LibraryEditResult> DeleteImageAsync(
        Guid gameId,
        long imageId,
        AuditActor actor,
        CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        var (error, row, zip, current, image) = await OpenImageAsync(gameId, imageId, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        using var _ = zip;
        if (image!.IsDefault)
            return new(DefaultImage, "The placeholder is managed automatically; upload a box art to replace it.");

        var list = current!.Images.Where(i => !ReferenceEquals(i, image)).ToList();
        var entries = new Dictionary<string, Func<Stream>>();
        if (image.IsCover)
        {
            if (list.Where(i => i.Type == ImageType.BoxArt).OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } boxArt)
                list[list.IndexOf(boxArt)] = boxArt with { IsCover = true };
            else if (list.Count > 0)
                list[0] = list[0] with { IsCover = true };
            else if (systems.ById(current.System) is { } handler)
            {
                var placeholder = MediaService.Default(handler);
                list.Add(placeholder.ToInfoJson(time.GetUtcNow(), isCover: true));
                entries[LibraryZip.ImageEntry(placeholder.FileName)] = () => new MemoryStream(placeholder.Bytes);
            }
        }

        var game = current with { Images = list, Metadata = libraryEditSupport.Touched(current) };
        var result = await libraryEditSupport.WriteZipAsync(zip, row!.ZipPath, game, entries).ConfigureAwait(false)
            ?? await libraryEditSupport.MirrorAsync(() => mirror.ReplaceAsync(libraryEditSupport.Mirror(row.ZipPath, game), CancellationToken.None), row.ZipPath).ConfigureAwait(false);
        if (LibraryEditSupport.FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.ImageDeleted, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath), Details: image.FileName));
        if (result.Code is null)
            logger.LogInformation("Image {FileName} deleted from game {GameId} ({ZipPath})", image.FileName, gameId, row.ZipPath);
        return result;
    }

    /// <summary>I7: makes one image the cover (any type, the placeholder too).</summary>
    internal async Task<LibraryEditResult> SetCoverAsync(
        Guid gameId,
        long imageId,
        AuditActor actor,
        CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        var (error, row, zip, current, image) = await OpenImageAsync(gameId, imageId, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        using var _ = zip;
        if (image!.IsCover)
            return Ok;

        var game = current! with
        {
            Images = current.Images.Select(i => i with { IsCover = ReferenceEquals(i, image) }).ToList(),
            Metadata = libraryEditSupport.Touched(current),
        };
        var result = await libraryEditSupport.WriteAsync(zip!, row!.ZipPath, game).ConfigureAwait(false);
        if (LibraryEditSupport.FilesChanged(result))
            audit.Record(new AuditEvent(AuditActions.CoverSet, actor, row.SystemId, gameId, AuditPaths.Library(row.ZipPath), Details: image.FileName));
        if (result.Code is null)
            logger.LogInformation("Image {FileName} is now the cover of game {GameId}", image.FileName, gameId);
        return result;
    }

    /// <summary>The game row, its open ZIP (the caller disposes it) and info.json, and the info.json image the DB row names.</summary>
    /// <returns>Error set (and nothing else) when the image or game isn't there or the ZIP can't be used.</returns>
    internal async Task<(LibraryEditResult? Error, GameRow? Row, LibraryZip? Zip, InfoJsonGame? Game, InfoJsonImage? Image)> OpenImageAsync(
        Guid gameId, long imageId, CancellationToken ct)
    {
        if (await images.GetAsync(gameId, imageId, ct).ConfigureAwait(false) is not { } imageRow
            || await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return (new(NotFound), null, null, null, null);
        if (libraryEditSupport.LegacyRefusal(row) is { } legacy)
            return (legacy, null, null, null, null);
        var target = libraryEditSupport.OpenForEdit(row);
        if (target.Code is not null)
            return (new(target.Code, target.Details), null, null, null, null);
        // The DB row only finds the image; the ZIP's info.json decides what's there (§6.1).
        if (target.Game!.Images.FirstOrDefault(i => string.Equals(i.FileName, imageRow.FileName, StringComparison.OrdinalIgnoreCase)) is not { } image)
        {
            target.Zip!.Dispose();
            return (new(NotFound, $"{row.ZipPath} no longer lists {imageRow.FileName}"), null, null, null, null);
        }
        return (null, row, target.Zip, target.Game, image);
    }

    /// <summary>§10.2 "Retry media" for one game, with its own circuit breaker. Nothing new found is still a success.</summary>
    internal async Task<LibraryEditResult> RetryMediaAsync(Guid gameId, AuditActor actor, CancellationToken ct)
    {
        using var held = jobLock.TryAcquire("Saving changes");
        if (held is null)
            return new(JobRunning);
        if (await games.GetAsync(gameId, ct).ConfigureAwait(false) is not { } row)
            return new(NotFound);
        if (libraryEditSupport.LegacyRefusal(row) is { } legacy)
            return legacy;
        var result = await mediaRetry.RetryAsync(row, new CircuitBreaker(), skipManualOnly: false, actor, ct).ConfigureAwait(false);
        if (result.Outcome == MediaRetryOutcome.Error)
            logger.LogError("Media retry for {ZipPath} failed: {Code} {Details}", row.ZipPath, result.Code, result.Details);
        return result.Outcome == MediaRetryOutcome.Error ? new(result.Code, result.Details) : Ok;
    }
}
