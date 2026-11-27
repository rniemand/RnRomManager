using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Media;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Library;

/// <summary>
/// Admin actions P1–P5, "Pull Cover Images": the IGDB cover of every linked game not tried yet (AC4: neither
/// <c>cover_image_pulled_at</c> nor <c>cover_image_checked_at</c>; without a pulled cover when <see cref="RetryMissing"/>)
/// becomes its only cover, and <c>actions.coverImagePulled</c> records it; an IGDB-side failure records
/// <c>actions.coverImageChecked</c> (AC5). Runs under the lock <see cref="JobRunner"/>
/// holds, so it writes each ZIP itself as <see cref="GameMediaRetry"/> does (never through <see cref="LibraryEditor"/>).
/// One job item per game; cancellable between games; an IGDB read failure or an open breaker fails the job.
/// </summary>
public sealed class PullCoversJob(
    IGameRepository games,
    IIgdbGames igdb,
    ILibraryMirror mirror,
    InfoJsonReader reader,
    ISystemRegistry systems,
    IJobItemRepository items,
    IIntegrityIssueRepository issues,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    TimeProvider time,
    ILogger<PullCoversJob> logger,
    IRomStorageSettings? zipSettings = null) : IJob
{
    public const string Pulled = "Pulled";
    public const string NoCover = "NoCover";
    public const string ImageLimit = "ImageLimit";
    public const string Skipped = "Skipped";
    public const string Error = "Error";

    /// <summary>P5: the job's error when the cover downloads' breaker opens.</summary>
    public const string ImagesUnreachable = "IGDB images couldn't be reached.";

    /// <summary>AC4, "Try Missing Images": also retries games whose IGDB cover couldn't be pulled before.</summary>
    public bool RetryMissing { get; set; }

    public JobType Type => JobType.PullCovers;
    public JobMode? Mode => null;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var candidates = await games.ListPullCoverCandidatesAsync(includeChecked: RetryMissing, cancellationToken).ConfigureAwait(false);
        context.Progress.SetTotal(candidates.Count);
        var breaker = new CircuitBreaker();
        var counts = new Dictionary<string, int> { [Pulled] = 0, [NoCover] = 0, [ImageLimit] = 0, [Skipped] = 0, [Error] = 0 };

        foreach (var batch in candidates.Chunk(IgdbClient.MaxBatch))
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            // P5: a failure here propagates and fails the job with the IGDB title.
            var found = (await igdb.GetGamesAsync(batch.Select(g => g.IgdbId!.Value).Distinct().ToList(), cancellationToken).ConfigureAwait(false))
                .ToDictionary(g => g.Id);
            foreach (var row in batch)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                context.Progress.SetCurrentItem(row.ZipPath);
                PullCoversJobOutcome outcome;
                try
                {
                    outcome = await PullAsync(row, found, breaker, context.Actor, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Pulling the cover of {ZipPath} failed", row.ZipPath);
                    outcome = new(Error, "Unexpected", ex.Message);
                }
                counts[outcome.Name]++;
                if (outcome.Name == Error)
                    logger.LogError("Pulling the cover of {ZipPath} failed: {Code} {Details}", row.ZipPath, outcome.Code, outcome.Details);
                await items.InsertAsync(new JobItem
                {
                    JobId = context.JobId,
                    FileId = row.Id,
                    GameId = row.Id,
                    SystemId = row.SystemId,
                    SourcePath = AuditPaths.Library(row.ZipPath),
                    Outcome = outcome.Name,
                    Reason = outcome.Code,
                    Details = outcome.Details,
                    CreatedAt = time.GetUtcNow().UtcDateTime,
                }, CancellationToken.None).ConfigureAwait(false);
                context.Progress.ItemDone();

                if (breaker.IsOpen)
                {
                    logger.LogWarning("Pull Cover Images stopped after {Done} of {Total} games", context.Progress.Current, candidates.Count);
                    throw new InvalidOperationException(ImagesUnreachable);
                }
            }
        }
        return new { pulled = counts[Pulled], noCover = counts[NoCover], imageLimit = counts[ImageLimit], skipped = counts[Skipped], error = counts[Error] };
    }

    private async Task<PullCoversJobOutcome> PullAsync(
        GameRow row,
        IReadOnlyDictionary<int, IgdbGameRating> found,
        CircuitBreaker breaker,
        AuditActor actor,
        CancellationToken ct)
    {
        var library = paths.Value.Roms;
        var target = LibraryTarget.OpenForWrite(library, row.ZipPath, reader, systems, row.Id, row.SystemId);
        if (target.Code is not null)
            return new(Error, target.Code, target.Details);
        using var existing = target.Zip!;
        var current = target.Game!;
        var path = SafePath.Resolve(library, row.ZipPath);

        if (systems.ById(current.System) is { } handler && RomStorage.IsPending(handler, current))
            return new(Skipped, Details: $"{row.ZipPath} waits for its storage migration");
        if (current.Actions?.CoverImagePulled is not null)
        {
            // The database lost it: the ZIP is the truth.
            var file = new FileInfo(path);
            return await MirrorAsync(new MirrorEntry(current, row.ZipPath, file.Length, file.LastWriteTimeUtc)).ConfigureAwait(false)
                ?? new PullCoversJobOutcome(Skipped, Details: "The cover was already pulled.");
        }
        if (!RetryMissing && current.Actions?.CoverImageChecked is not null)
        {
            // AC5: the database lost the check: the ZIP is the truth.
            var file = new FileInfo(path);
            return await MirrorAsync(new MirrorEntry(current, row.ZipPath, file.Length, file.LastWriteTimeUtc)).ConfigureAwait(false)
                ?? new PullCoversJobOutcome(Skipped, Details: "The cover was already checked.");
        }
        if (current.IgdbId is not > 0 || current.IgdbId != row.IgdbId)
            return new(Skipped, Details: "The ZIP's IGDB link differs from the database's.");
        if (!found.TryGetValue(current.IgdbId.Value, out var igdbGame) || igdbGame.CoverImageId is not { } imageId)
            return await CheckedAsync(row, current, existing, path, new(NoCover)).ConfigureAwait(false);

        var download = await igdb.DownloadCoverAsync(imageId, ct).ConfigureAwait(false);
        if (download.Status is null or >= 500)
            breaker.RecordFailure();
        else if (download.Status == 200)
            breaker.RecordSuccess();
        if (download.Bytes is not { } bytes)
        {
            PullCoversJobOutcome downloadFailed = new(Error, "DownloadFailed", download.Error);
            // AC5: only an answer from IGDB (oversize 200 or 4xx) is recorded; a network error or 5xx may be an outage.
            return download.Status is 200 or (>= 400 and < 500)
                ? await CheckedAsync(row, current, existing, path, downloadFailed).ConfigureAwait(false)
                : downloadFailed;
        }
        if (ImageFormats.Read(bytes) is not var (format, width, height))
            return await CheckedAsync(row, current, existing, path,
                new(Error, "ImageUnreadable", "The IGDB cover isn't a PNG or JPEG whose size can be read.")).ConfigureAwait(false);

        var now = time.GetUtcNow();
        var url = IgdbClient.CoverUrl(imageId);
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes));
        // B14: replacing the placeholder cover, or giving a game its first image, resolves its box art issues.
        var resolves = current.Images.Count == 0 || current.Images.Any(i => i.IsDefault && i.IsCover);
        var entries = new Dictionary<string, Func<Stream>>();
        List<InfoJsonImage> images;
        string fileName;
        if (current.Images.FirstOrDefault(i => i.Sha1 == sha1) is { } same)
        {
            images = current.Images.Select(i => i with { IsCover = i == same }).ToList();
            fileName = same.FileName;
        }
        else
        {
            images = current.Images.Where(i => !i.IsDefault).Select(i => i with { IsCover = false }).ToList();
            fileName = GameImages.NextName(images.Select(i => i.FileName), ImageType.BoxArt, ImageFormats.Extension(format));
            images.Add(new InfoJsonImage
            {
                FileName = fileName,
                Type = ImageType.BoxArt,
                Source = InfoJsonImage.SourceUpload,
                SourceUrl = url,
                IsDefault = false,
                IsCover = true,
                SizeBytes = bytes.Length,
                Sha1 = sha1,
                Width = width,
                Height = height,
                DateAdded = now,
            });
            if (images.Count > GameImages.MaxImages)
                return await CheckedAsync(row, current, existing, path,
                    new(ImageLimit, Details: $"A game can have at most {GameImages.MaxImages} images.")).ConfigureAwait(false);
            entries[LibraryZip.ImageEntry(fileName)] = () => new MemoryStream(bytes);
        }

        var game = current with
        {
            Images = images,
            Actions = (current.Actions ?? new InfoJsonActions()) with { CoverImagePulled = InfoJsonActions.Format(now) },
            Metadata = current.Metadata with { UpdatedAt = now, AppVersion = AppInfo.Version },
        };

        // Not cancellable once the write starts: a ZIP is never left half-applied.
        try
        {
            await WriteAsync(row.ZipPath, path, existing, new LibraryZipChanges(game, entries)).ConfigureAwait(false);
        }
        catch (LibraryZipVerifyException ex)
        {
            return new(Error, LibraryEditResult.VerifyFailed, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(Error, LibraryEditResult.IoError, ex.Message);
        }
        audit.Record(new AuditEvent(AuditActions.MediaFetched, actor, game.System, row.Id, AuditPaths.Library(row.ZipPath),
            Details: $"IGDB cover {fileName}"));

        var info = new FileInfo(path);
        if (await MirrorAsync(new MirrorEntry(game, row.ZipPath, info.Length, info.LastWriteTimeUtc)).ConfigureAwait(false) is { } failed)
            return failed;
        if (resolves)
        {
            try
            {
                foreach (var type in new[] { IntegrityIssueType.DefaultBoxArt, IntegrityIssueType.MissingBoxArt })
                    await issues.ResolveOpenAsync(row.ZipPath, type, now.UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Resolving the box art issues of {ZipPath} failed", row.ZipPath);
            }
        }
        logger.LogInformation("IGDB cover of game {GameId} pulled ({ZipPath}): {Image}", row.Id, row.ZipPath, fileName);
        return new(Pulled);
    }

    private async Task WriteAsync(
        string zipPath,
        string path,
        LibraryZip existing,
        LibraryZipChanges changes)
    {
        var writer = new LibraryZipWriter(reader, zipSettings?.Compression(changes.Info.System) ?? ZipCompression.Med);
        if (LibraryTarget.NeedsCheck(systems, zipPath, changes.Info.System))
            await writer.WriteCheckedAsync(paths.Value.Roms, path, existing, changes).ConfigureAwait(false);
        else
            await writer.WriteAsync(path, existing, changes).ConfigureAwait(false);
    }

    /// <summary>
    /// AC5: records an IGDB-side failure as <c>actions.coverImageChecked</c> = now in the ZIP and the database, so default
    /// runs skip the game. No audit event (nothing was fetched); a failed write is logged and the game tried next run.
    /// </summary>
    /// <returns><paramref name="outcome"/>, unchanged.</returns>
    private async Task<PullCoversJobOutcome> CheckedAsync(
        GameRow row,
        InfoJsonGame current,
        LibraryZip existing,
        string path,
        PullCoversJobOutcome outcome)
    {
        var now = time.GetUtcNow();
        var game = current with
        {
            Actions = (current.Actions ?? new InfoJsonActions()) with { CoverImageChecked = InfoJsonActions.Format(now) },
            Metadata = current.Metadata with { UpdatedAt = now, AppVersion = AppInfo.Version },
        };
        try
        {
            await WriteAsync(row.ZipPath, path, existing, new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>())).ConfigureAwait(false);
            var info = new FileInfo(path);
            await mirror.ReplaceAsync(new MirrorEntry(game, row.ZipPath, info.Length, info.LastWriteTimeUtc), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording the cover check of {ZipPath} failed", row.ZipPath);
        }
        return outcome;
    }

    /// <returns>null when mirrored, else the <c>DbWriteFailed</c> outcome.</returns>
    private async Task<PullCoversJobOutcome?> MirrorAsync(MirrorEntry entry)
    {
        try
        {
            await mirror.ReplaceAsync(entry, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return new(Error, LibraryEditResult.DbWriteFailed, ex.Message);
        }
    }
}
