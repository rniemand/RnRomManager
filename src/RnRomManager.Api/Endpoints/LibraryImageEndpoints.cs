using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using RnRomManager.Api.Auth;
using RnRomManager.Api.Http;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Systems;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Endpoints;

/// <summary>The image routes of <c>/api/games/{id}</c> (I5–I7, U1–U5, C3, B10) and the upload helpers other endpoints share.</summary>
public static class LibraryImageEndpoints
{
    /// <summary>B10: <c>&lt;title&gt;-&lt;imageId&gt;.&lt;ext&gt;</c>; the suffix is added after sanitizing (which cuts to 180), so it is never lost.</summary>
    public static string ImageDownloadName(string displayTitle, ImageRow image)
    {
        var title = FileNameSanitizer.Sanitize(displayTitle);
        return $"{(title.Length == 0 ? "image" : title)}-{image.Id}{Path.GetExtension(image.FileName)}";
    }

    public static RouteGroupBuilder MapLibraryImageEndpoints(this RouteGroupBuilder api)
    {
        var games = api.MapGroup("/games");

        // B10: ?download=true sends it as an attachment named after the game.
        games.MapGet("/{id:guid}/images/{imageId:long}", async (Guid id, long imageId, bool? download, HttpContext ctx, IGameRepository repository,
            IImageRepository images, LibraryFiles files) =>
        {
            if (await images.GetAsync(id, imageId, ctx.RequestAborted) is not { } image
                || await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);

            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            // A 304 for a matching If-None-Match is answered by the stream result itself.
            return files.OpenEntry(ctx, game, LibraryZip.ImageEntry(image.FileName), ImageFormats.MaxBytes, out var stream) is { } problem
                ? problem
                : Results.Stream(stream!, ImageFormats.ContentType(image.FileName), download == true ? ImageDownloadName(game.DisplayTitle, image) : null,
                    entityTag: new EntityTagHeaderValue($"\"{image.Sha1}\""));
        });

        // C3: the cached WebP of the current cover; a stale sha1 is a 404, so only the database's value builds a path.
        games.MapGet("/{id:guid}/cover/{sha1}", async (Guid id, string sha1, HttpContext ctx, IGameRepository repository,
            IImageRepository images, LibraryFiles files, CoverCache cache, ILogger<CoverCache> logger) =>
        {
            var cover = (await images.ListByGameAsync(id, ctx.RequestAborted)).Where(i => i.IsCover).OrderBy(i => i.Id).FirstOrDefault();
            if (cover is null || !string.Equals(cover.Sha1, sha1, StringComparison.Ordinal)
                || await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);

            var etag = new EntityTagHeaderValue($"\"{cover.Sha1}\"");
            if (cache.CachedPath(id, cover.Sha1) is { } path)
            {
                ctx.Response.Headers.CacheControl = CoverCacheControl;
                return Results.File(path, "image/webp", entityTag: etag);
            }

            var entry = LibraryZip.ImageEntry(cover.FileName);
            if (files.OpenEntry(ctx, game, entry, ImageFormats.MaxBytes, out var stream) is { } problem)
                return problem;
            CoverCacheResult result;
            await using (stream)
                result = await cache.GenerateAsync(id, cover.Sha1, stream!, ctx.RequestAborted);

            if (result.Webp is { } webp)
            {
                ctx.Response.Headers.CacheControl = CoverCacheControl;
                return Results.Bytes(webp, "image/webp", entityTag: etag);
            }

            logger.LogWarning("The cover of game {GameId} couldn't be converted to WebP; serving the original.", id);
            if (files.OpenEntry(ctx, game, entry, ImageFormats.MaxBytes, out var original) is { } reopenProblem)
                return reopenProblem;
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.Stream(original!, ImageFormats.ContentType(cover.FileName), entityTag: etag);
        });

        // I5–I7: image management. The upload reads the form itself (no IFormFile binding), so the group's own
        // X-XSRF-TOKEN filter is the antiforgery check, as for every other mutation.
        games.MapPost("/{id:guid}/images", async (Guid id, HttpContext ctx, LibraryEditor editor, IGameRepository repository,
            IRomRepository roms, IImageRepository images, JobRunner runner, IntegrityJobFactory integrity, ILoggerFactory loggers,
            ISystemRegistry systems, JobLock jobLock, IIntegrityIssueRepository issues, TimeProvider time) =>
        {
            var (form, problem) = await ReadImageFormAsync(ctx);
            if (problem is not null)
                return problem;
            var errors = new Dictionary<string, string[]>();
            // Exact names only: Enum.TryParse would also accept "0" or "boxart".
            var type = form!["type"].ToString() switch { "BoxArt" => ImageType.BoxArt, "Snap" => ImageType.Snap, _ => (ImageType?)null };
            if (type is null)
                errors["type"] = ["Choose BoxArt or Snap."];
            var bytes = await ReadImageFileAsync(form, errors, ctx.RequestAborted);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.AddImageAsync(id, type!.Value, bytes!, AuditActor.From(ctx.User), ctx.RequestAborted);
            await StartGameCheckAsync(result, id, ctx, repository, runner, integrity, loggers, systems, jobLock, issues, time);
            return result.Code is null ? await LibraryEndpoints.DetailAsync(id, ctx, repository, roms, images) : LibraryEndpoints.ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(UploadRequestLimit));

        // U1–U5: the server downloads the image (public addresses only), then stores it exactly like an upload.
        games.MapPost("/{id:guid}/images/from-url", async (Guid id, ImageUrlRequest request, HttpContext ctx, ImageUrlDownloader downloader,
            LibraryEditor editor, IGameRepository repository, IRomRepository roms, IImageRepository images, JobRunner runner,
            IntegrityJobFactory integrity, ILoggerFactory loggers, ISystemRegistry systems, JobLock jobLock, IIntegrityIssueRepository issues,
            TimeProvider time) =>
        {
            var errors = Validate(request, out var url, out var type);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            if (await repository.GetAsync(id, ctx.RequestAborted) is null)
                return Problems.NotFound(ctx);
            var download = await downloader.DownloadAsync(url!, ctx.RequestAborted);
            if (download.Bytes is not { } bytes)
                return Problems.Create(ctx, download.Status, download.Code!, download.Title!);
            if (DownloadedImageError(bytes) is { } bytesError)
                return Problems.Validation(ctx, "url", bytesError);
            var result = await editor.AddImageAsync(id, type, bytes, AuditActor.From(ctx.User), ctx.RequestAborted, request.Url);
            await StartGameCheckAsync(result, id, ctx, repository, runner, integrity, loggers, systems, jobLock, issues, time);
            return result.Code is null ? await LibraryEndpoints.DetailAsync(id, ctx, repository, roms, images) : LibraryEndpoints.ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // IGDB suggestions S8: read-only candidates for the Suggest Images dialog; adding one is the from-url route above.
        games.MapGet("/{id:guid}/image-suggestions", async (Guid id, string? type, HttpContext ctx, IGameRepository repository,
            IImageSuggestions suggestions) =>
        {
            ImageType? parsed = type switch { "BoxArt" => ImageType.BoxArt, "Snap" => ImageType.Snap, _ => null };
            if (parsed is null)
                return Problems.Validation(ctx, "type", "Choose BoxArt or Snap.");
            if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            if (!suggestions.Configured)
                return Problems.Create(ctx, StatusCodes.Status409Conflict, LibraryEndpoints.IgdbNotConfigured,
                    "Image suggestions need Sources:IgdbClientId and Sources:IgdbClientSecret.");
            var result = await suggestions.SuggestAsync(game.DisplayTitle, game.SystemId, parsed.Value, ctx.RequestAborted);
            return result.Suggestions is { } list
                ? Results.Ok(new { suggestions = list })
                : Problems.Create(ctx, StatusCodes.Status502BadGateway, LibraryEndpoints.IgdbFailed, result.FailedTitle!);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapDelete("/{id:guid}/images/{imageId:long}", async (Guid id, long imageId, HttpContext ctx, LibraryEditor editor,
            IGameRepository repository, IRomRepository roms, IImageRepository images) =>
        {
            var result = await editor.DeleteImageAsync(id, imageId, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await LibraryEndpoints.DetailAsync(id, ctx, repository, roms, images) : LibraryEndpoints.ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapPost("/{id:guid}/images/{imageId:long}/cover", async (Guid id, long imageId, HttpContext ctx, LibraryEditor editor,
            IGameRepository repository, IRomRepository roms, IImageRepository images) =>
        {
            var result = await editor.SetCoverAsync(id, imageId, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await LibraryEndpoints.DetailAsync(id, ctx, repository, roms, images) : LibraryEndpoints.ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);
        return api;
    }

    /// <summary>The upload's whole request: the 8 MiB image plus room for the multipart framing and the <c>type</c> field.</summary>
    public const long UploadRequestLimit = ImageFormats.MaxBytes + 64 * 1024;

    /// <summary>
    /// B14: once the first real box art replaced the placeholder cover (and the editor released the job lock), a deep
    /// check of that game clears its <c>DefaultBoxArt</c> issue. G1: an external game's check would re-hash every external
    /// file, so its issue is resolved directly under the job lock instead. Skipped, never queued, when another job holds
    /// the lock; the response is the game detail whatever happens here.
    /// </summary>
    private static async Task StartGameCheckAsync(
        LibraryEditResult result,
        Guid id,
        HttpContext ctx,
        IGameRepository repository,
        JobRunner runner,
        IntegrityJobFactory integrity,
        ILoggerFactory loggers,
        ISystemRegistry systems,
        JobLock jobLock,
        IIntegrityIssueRepository issues,
        TimeProvider time)
    {
        if (result.Code is not null || !result.ReplacedDefaultCover)
            return;
        var logger = loggers.CreateLogger("RnRomManager.Api.Endpoints.GameCheck");
        try
        {
            if (await repository.GetAsync(id, CancellationToken.None) is not { } row)
                return;
            if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            {
                using var held = jobLock.TryAcquire("Saving changes");
                if (held is null)
                    logger.LogInformation("Resolving DefaultBoxArt of {ZipPath} skipped: another job is running", row.ZipPath);
                else
                    await issues.ResolveOpenAsync(row.ZipPath, IntegrityIssueType.DefaultBoxArt, time.GetUtcNow().UtcDateTime, CancellationToken.None);
                return;
            }
            var started = await runner.StartAsync(integrity(JobMode.Game, row.ZipPath), JobTrigger.Manual, EndpointUser.Id(ctx.User),
                CancellationToken.None);
            if (started is null)
                logger.LogInformation("Game check of {ZipPath} skipped: another job is running", row.ZipPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Game check of game {GameId} could not start", id);
        }
    }

    private static IResult TooLarge(HttpContext ctx) =>
        Problems.Create(ctx, StatusCodes.Status413PayloadTooLarge, "PayloadTooLarge", "The upload is larger than 8 MiB.");

    /// <summary>
    /// The multipart form of an image upload (I5, B8), or the problem to answer: 413 when it's too large, 400 <c>file</c>
    /// when it isn't a form. Endpoints using it also carry <c>RequestSizeLimit(UploadRequestLimit)</c>.
    /// </summary>
    internal static async Task<(IFormCollection? Form, IResult? Problem)> ReadImageFormAsync(HttpContext ctx)
    {
        // Kestrel enforces the size-limit metadata; this also covers hosts that don't (TestServer) and
        // chunked bodies, which only fail while the form is read.
        if (ctx.Request.ContentLength > UploadRequestLimit)
            return (null, TooLarge(ctx));
        if (!ctx.Request.HasFormContentType)
            return (null, Problems.Validation(ctx, "file", "Send the image as multipart/form-data."));
        try
        {
            return (await ctx.Request.ReadFormAsync(ctx.RequestAborted), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            return (null, TooLarge(ctx));
        }
    }

    /// <summary>The form's <c>file</c> (≤ 8 MiB; format and dimensions are checked later), or null with an error added.</summary>
    internal static async Task<byte[]?> ReadImageFileAsync(IFormCollection form, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            errors["file"] = ["Choose an image file."];
        else if (file.Length > ImageFormats.MaxBytes)
            errors["file"] = ["The image must be at most 8 MiB."];
        if (errors.ContainsKey("file"))
            return null;

        using var buffer = new MemoryStream((int)file!.Length);
        await file.CopyToAsync(buffer, cancellationToken);
        return ImageFormats.ConvertWebp(buffer.ToArray());
    }

    /// <summary>U2/B9: at most 1024 characters (<c>images.source_url</c>), absolute http(s), no user info; else null.</summary>
    public static Uri? ParseImageUrl(string? text) =>
        text is { Length: > 0 and <= 1024 } && Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https" && parsed.UserInfo.Length == 0
                ? parsed
                : null;

    /// <summary>U3/B9: the <c>url</c> error for downloaded bytes, or null when they're a storable PNG/JPEG.</summary>
    public static string? DownloadedImageError(byte[] bytes) =>
        bytes.Length > ImageFormats.MaxBytes ? "The image must be at most 8 MiB."
        : bytes.Length == 0 || ImageFormats.Detect(bytes) is null || ImageFormats.Read(bytes) is null ? "The URL didn't return a PNG or JPEG image."
        : null;

    /// <summary>
    /// U2: <c>url</c> required, at most 1024 characters (<c>images.source_url</c>), absolute http(s) without user info;
    /// <c>type</c> exactly <c>BoxArt</c> or <c>Snap</c>, as for the upload. Nothing is downloaded when this fails.
    /// </summary>
    public static Dictionary<string, string[]> Validate(ImageUrlRequest request, out Uri? url, out ImageType type)
    {
        var errors = new Dictionary<string, string[]>();
        url = ParseImageUrl(request.Url);
        if (url is null)
            errors["url"] = ["Enter an http or https image URL."];
        ImageType? parsedType = request.Type switch { "BoxArt" => ImageType.BoxArt, "Snap" => ImageType.Snap, _ => null };
        if (parsedType is null)
            errors["type"] = ["Choose BoxArt or Snap."];
        type = parsedType ?? default;
        return errors;
    }

    private const string CoverCacheControl = "private, max-age=31536000, immutable";
}
