using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;
using RnRomManager.Api.Auth;
using RnRomManager.Api.Http;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Database;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// Library reads for every signed-in user and Admin edits (spec §23 <c>/api/systems</c>, <c>/api/games*</c>): lists and details come from
/// the DB mirror, image and ROM bytes are streamed from the game's ZIP.
/// </summary>
public static class LibraryEndpoints
{
    public static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/systems", async (HttpContext ctx, ISystemRegistry systems, IGameRepository games) =>
        {
            var stats = (await games.ListSystemStatsAsync(ctx.RequestAborted)).ToDictionary(s => s.SystemId);
            return Results.Ok(systems.All.OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase).Select(handler => stats.TryGetValue(handler.Id, out var s)
                ? new { id = handler.Id, displayName = handler.DisplayName, gameCount = s.GameCount, romCount = s.RomCount, regions = s.Regions }
                : new { id = handler.Id, displayName = handler.DisplayName, gameCount = 0, romCount = 0, regions = (IReadOnlyList<string>)[] }));
        });

        var games = api.MapGroup("/games");

        games.MapGet("", async (string? system, string? letter, string? region, string? q, string? sort, string? dir, int? page,
            int? pageSize, string? favourite, string? collection, HttpContext ctx, ISystemRegistry systems, IGameRepository repository,
            ICollectionRepository collections) =>
        {
            var (query, errors) = ParseQuery(system, letter, region, q, sort, dir, page, pageSize, systems, favourite, collection);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            // B15: another user's collection answers 404 like a missing one.
            if (query.CollectionId is { } collectionId
                && await collections.GetAsync(collectionId, CollectionEndpoints.UserId(ctx), ctx.RequestAborted) is null)
                return Problems.NotFound(ctx);
            var result = await repository.ListAsync(query, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, game => new
            {
                id = game.Id,
                system = game.SystemId,
                systemName = systems.ById(game.SystemId)?.DisplayName ?? game.SystemId,
                displayTitle = game.DisplayTitle,
                releaseYear = game.ReleaseYear,
                regions = game.Regions,
                romCount = game.RomCount,
                primarySizeBytes = game.PrimarySizeBytes,
                boxArtUrl = game.BoxArtImageId is { } imageId ? ImageUrl(game.Id, imageId) : null,
                rating = game.Rating,
                favourite = game.Favourite,
                matchedOn = game.MatchedOn is { } match ? new { field = match.Field, value = match.Value } : null,
            }));
        });

        games.MapGet("/{id:guid}", (Guid id, HttpContext ctx, IGameRepository repository, IRomRepository roms, IImageRepository images) =>
            DetailAsync(id, ctx, repository, roms, images));

        games.MapGet("/{id:guid}/images/{imageId:long}", async (Guid id, long imageId, HttpContext ctx, IGameRepository repository,
            IImageRepository images, LibraryFiles files) =>
        {
            if (await images.GetAsync(id, imageId, ctx.RequestAborted) is not { } image
                || await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);

            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            // A 304 for a matching If-None-Match is answered by the stream result itself.
            return files.OpenEntry(ctx, game, LibraryZip.ImageEntry(image.FileName), ImageFormats.MaxBytes, out var stream) is { } problem
                ? problem
                : Results.Stream(stream!, ImageFormats.ContentType(image.FileName), entityTag: new EntityTagHeaderValue($"\"{image.Sha1}\""));
        });

        games.MapGet("/{id:guid}/download", async (Guid id, HttpContext ctx, IGameRepository repository, LibraryFiles files) =>
        {
            if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            if (files.OpenZip(ctx, game, out var stream) is { } problem)
                return problem;
            var name = game.ZipPath[(game.ZipPath.LastIndexOf('/') + 1)..];
            // fileDownloadName sets Content-Disposition with both filename and RFC 5987 filename*.
            return Results.Stream(stream!, "application/zip", name, File.GetLastWriteTimeUtc(stream!.Name), enableRangeProcessing: true);
        });

        // B16: the caller's collections holding this game.
        games.MapGet("/{id:guid}/collections", async (Guid id, HttpContext ctx, IGameRepository repository, ICollectionRepository collections) =>
        {
            if (await repository.GetAsync(id, ctx.RequestAborted) is null)
                return Problems.NotFound(ctx);
            var list = await collections.ListForGameAsync(id, CollectionEndpoints.UserId(ctx), ctx.RequestAborted);
            return Results.Ok(list.Select(c => new { id = c.Id, name = c.Name }));
        });

        games.MapGet("/{id:guid}/info-json", async (Guid id, HttpContext ctx, IGameRepository repository, LibraryFiles files) =>
        {
            if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            // Returned as stored, even when it isn't valid JSON (GP4 shows the raw text then).
            return files.ReadEntry(ctx, game, LibraryZip.InfoJsonEntry, InfoJsonReader.MaxBytes, out var bytes) is { } problem
                ? problem
                : Results.Bytes(bytes!, "application/json");
        });

        games.MapGet("/{id:guid}/roms/{romId:long}/download", async (Guid id, long romId, HttpContext ctx, IGameRepository repository,
            IRomRepository roms, LibraryFiles files) =>
        {
            if (await roms.GetAsync(id, romId, ctx.RequestAborted) is not { } rom
                || await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            return files.OpenEntry(ctx, game, LibraryZip.RomEntry(rom.FileName), rom.SizeBytes, out var stream) is { } problem
                ? problem
                : Results.Stream(stream!, "application/octet-stream", rom.FileName);
        });

        // B4–B7 release notes: any signed-in user, like ratings; not audited.
        games.MapGet("/{id:guid}/roms/{romId:long}/note", async (Guid id, long romId, HttpContext ctx, IGameRepository repository,
            IRomRepository roms, LibraryFiles files) =>
        {
            if (await roms.GetAsync(id, romId, ctx.RequestAborted) is not { } rom
                || await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            return files.ReadNote(ctx, game, rom.FileName, out var bytes) is { } problem
                ? problem
                : Results.Bytes(bytes!, "text/markdown; charset=utf-8");
        });

        games.MapPut("/{id:guid}/roms/{romId:long}/note", async (Guid id, long romId, JsonElement body, HttpContext ctx, LibraryEditor editor) =>
        {
            var (text, errors) = ParseNote(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            return ToResult(ctx, await editor.SaveNoteAsync(id, romId, text!, AuditActor.From(ctx.User).UserId, ctx.RequestAborted));
        });

        games.MapDelete("/{id:guid}/roms/{romId:long}/note", async (Guid id, long romId, HttpContext ctx, LibraryEditor editor) =>
            ToResult(ctx, await editor.DeleteNoteAsync(id, romId, AuditActor.From(ctx.User).UserId, ctx.RequestAborted)));

        // Admin edits (§11, D28, D35): each takes the job lock inside LibraryEditor.
        games.MapPatch("/{id:guid}", async (Guid id, JsonElement body, HttpContext ctx, LibraryEditor editor, IGameRepository repository,
            IRomRepository roms, IImageRepository images) =>
        {
            var (patch, errors) = ParsePatch(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.PatchAsync(id, patch, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.LibraryEditor);

        // Any signed-in user (shared per game); takes the job lock and rewrites the ZIP like every edit.
        games.MapPatch("/{id:guid}/rating", async (Guid id, JsonElement body, HttpContext ctx, LibraryEditor editor, IGameRepository repository,
            IRomRepository roms, IImageRepository images) =>
        {
            var (patch, errors) = ParseRating(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.PatchAsync(id, patch, null, ctx.RequestAborted); // rating/favourite: not audited
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        });

        games.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, LibraryEditor editor) =>
            ToResult(ctx, await editor.DeleteGameAsync(id, AuditActor.From(ctx.User), ctx.RequestAborted))).RequireAuthorization(AuthSetup.LibraryEditor);

        games.MapDelete("/{id:guid}/roms/{romId:long}", async (Guid id, long romId, HttpContext ctx, LibraryEditor editor) =>
            ToResult(ctx, await editor.DeleteReleaseAsync(id, romId, AuditActor.From(ctx.User), ctx.RequestAborted))).RequireAuthorization(AuthSetup.LibraryEditor);

        games.MapPost("/{id:guid}/roms/{romId:long}/move", async (Guid id, long romId, MoveReleaseRequest request, HttpContext ctx,
            LibraryEditor editor) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.MoveReleaseAsync(id, romId, request.TargetGameId, request.NewGameTitle?.Trim(), AuditActor.From(ctx.User),
                ctx.RequestAborted);
            return result.Code is null ? Results.Ok(new { targetGameId = result.GameId }) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.LibraryEditor);

        games.MapPost("/{id:guid}/media/retry", async (Guid id, HttpContext ctx, LibraryEditor editor, IGameRepository repository,
            IRomRepository roms, IImageRepository images) =>
        {
            var result = await editor.RetryMediaAsync(id, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.LibraryEditor);

        // I5–I7: image management. The upload reads the form itself (no IFormFile binding), so the group's own
        // X-XSRF-TOKEN filter is the antiforgery check, as for every other mutation.
        games.MapPost("/{id:guid}/images", async (Guid id, HttpContext ctx, LibraryEditor editor, IGameRepository repository,
            IRomRepository roms, IImageRepository images) =>
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
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.LibraryEditor).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(UploadRequestLimit));

        // U1–U5: the server downloads the image (public addresses only), then stores it exactly like an upload.
        games.MapPost("/{id:guid}/images/from-url", async (Guid id, ImageUrlRequest request, HttpContext ctx, ImageUrlDownloader downloader,
            LibraryEditor editor, IGameRepository repository, IRomRepository roms, IImageRepository images) =>
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
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["url"] = [bytesError] });
            var result = await editor.AddImageAsync(id, type, bytes, AuditActor.From(ctx.User), ctx.RequestAborted, request.Url);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.LibraryEditor);

        games.MapDelete("/{id:guid}/images/{imageId:long}", async (Guid id, long imageId, HttpContext ctx, LibraryEditor editor,
            IGameRepository repository, IRomRepository roms, IImageRepository images) =>
        {
            var result = await editor.DeleteImageAsync(id, imageId, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.LibraryEditor);

        games.MapPost("/{id:guid}/images/{imageId:long}/cover", async (Guid id, long imageId, HttpContext ctx, LibraryEditor editor,
            IGameRepository repository, IRomRepository roms, IImageRepository images) =>
        {
            var result = await editor.SetCoverAsync(id, imageId, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.LibraryEditor);

        api.MapPost("/jobs/media-retry", async (HttpContext ctx, JobRunner runner, MediaRetryJob job) =>
        {
            var started = await runner.StartAsync(job, JobTrigger.Manual, DatEndpoints.UserId(ctx.User), ctx.RequestAborted);
            return started is null ? DatEndpoints.JobRunning(ctx) : Results.Accepted(value: new { jobId = started.Id });
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapPost("/{id:guid}/merge", async (Guid id, MergeRequest request, HttpContext ctx, LibraryEditor editor) =>
        {
            if (request.TargetGameId is not { } target || target == Guid.Empty)
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["targetGameId"] = ["A target game is required."] });
            var result = await editor.MergeAsync(id, target, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? Results.Ok(new { targetGameId = result.GameId }) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.LibraryEditor);

        return api;
    }

    /// <summary>The upload's whole request: the 8 MiB image plus room for the multipart framing and the <c>type</c> field.</summary>
    public const long UploadRequestLimit = ImageFormats.MaxBytes + 64 * 1024;

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
            return (null, Problems.Validation(ctx, new Dictionary<string, string[]> { ["file"] = ["Send the image as multipart/form-data."] }));
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
        return buffer.ToArray();
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

    private static async Task<IResult> DetailAsync(Guid id, HttpContext ctx, IGameRepository repository, IRomRepository roms, IImageRepository images)
    {
        if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
            return Problems.NotFound(ctx);
        var romRows = await roms.ListByGameAsync(id, ctx.RequestAborted);
        var imageRows = await images.ListByGameAsync(id, ctx.RequestAborted);
        var systemName = ctx.RequestServices.GetRequiredService<ISystemRegistry>().ById(game.SystemId)?.DisplayName ?? game.SystemId;
        var notes = ctx.RequestServices.GetRequiredService<LibraryFiles>().NoteEntries(game);
        return Results.Ok(ToDetail(game, systemName, romRows, imageRows, notes));
    }

    private static IResult ToResult(HttpContext ctx, LibraryEditResult result) =>
        result.Code is null ? Results.NoContent() : ToProblem(ctx, result);

    /// <summary>§23 mutation bullet: 409 for the lock and unwritable ZIPs, 404 for a stale id, 500 for write failures.</summary>
    internal static IResult ToProblem(HttpContext ctx, LibraryEditResult result, IReadOnlyDictionary<string, object?>? extensions = null) =>
        result.Code switch
        {
            LibraryEditResult.ValidationFailed => Problems.Validation(ctx, result.Errors!, extensions),
            LibraryEditResult.NotFound => Problems.NotFound(ctx, extensions),
            LibraryEditResult.JobRunning or LibraryEditResult.TooManyImages or LibraryFiles.NoteTooLarge or LibraryEditResult.DuplicateImage or LibraryEditResult.DefaultImage
                or LibraryTarget.InfoJsonNewerSchema or LibraryTarget.TargetZipUnreadable =>
                Problems.Create(ctx, StatusCodes.Status409Conflict, result.Code, result.Code, extensions),
            _ => Problems.Create(ctx, StatusCodes.Status500InternalServerError, result.Code!, result.Code!, extensions),
        };

    private const string TitleRule = "Must contain 1–255 characters and produce a nonempty filename.";

    /// <summary>§6.1 game title: 1–255 chars after trim, must not sanitize to empty.</summary>
    private static bool IsValidTitle(string? trimmed) =>
        trimmed is { Length: >= 1 and <= 255 } && FileNameSanitizer.Sanitize(trimmed).Length > 0;

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

    /// <summary><c>move</c> body (§23): <c>{targetGameId}</c> or <c>{newGameTitle}</c>, not both.</summary>
    public static Dictionary<string, string[]> Validate(MoveReleaseRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var hasTarget = request.TargetGameId is { } target && target != Guid.Empty;
        if (hasTarget == request.NewGameTitle is not null)
            errors["targetGameId"] = ["Give either targetGameId or newGameTitle."];
        else if (request.NewGameTitle is not null && !IsValidTitle(request.NewGameTitle.Trim()))
            errors["newGameTitle"] = [TitleRule];
        return errors;
    }

    /// <summary>
    /// B5: <c>{text: string}</c>, line endings normalised to <c>\n</c>; whitespace-only becomes <c>""</c> (delete);
    /// more than <see cref="LibraryZip.MaxNoteBytes"/> UTF-8 bytes after normalisation is an error.
    /// </summary>
    public static (string? Text, Dictionary<string, string[]> Errors) ParseNote(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
        {
            errors["text"] = ["A text string is required."];
            return (null, errors);
        }
        var text = value.GetString()!.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(text))
            return ("", errors);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > LibraryZip.MaxNoteBytes)
            errors["text"] = [$"The note must be at most {LibraryZip.MaxNoteBytes:N0} bytes (UTF-8)."];
        return (text, errors);
    }

    /// <summary>
    /// <c>PATCH /api/games/{id}</c> body (§23): an absent property is left unchanged; <c>releaseYear</c> and
    /// <c>description</c> may be null to clear them. Lists are trimmed and made distinct before the limits are checked.
    /// </summary>
    public static (GamePatch Patch, Dictionary<string, string[]> Errors) ParsePatch(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        var patch = new GamePatch();
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors[""] = ["A JSON object is required."];
            return (patch, errors);
        }

        if (body.TryGetProperty("title", out var title))
        {
            var value = title.ValueKind == JsonValueKind.String ? title.GetString()!.Trim() : null;
            if (!IsValidTitle(value))
                errors["title"] = [TitleRule];
            patch = patch with { Title = value };
        }

        if (body.TryGetProperty("releaseYear", out var year))
        {
            if (year.ValueKind == JsonValueKind.Null)
                patch = patch with { SetReleaseYear = true };
            else if (year.ValueKind == JsonValueKind.Number && year.TryGetInt32(out var value) && value is >= 1970 and <= 2100)
                patch = patch with { SetReleaseYear = true, ReleaseYear = value };
            else
                errors["releaseYear"] = ["Must be null or a year 1970–2100."];
        }

        patch = patch with
        {
            Developers = ParseList(body, "developers", errors),
            Publishers = ParseList(body, "publishers", errors),
            Genres = ParseList(body, "genres", errors),
        };

        if (body.TryGetProperty("description", out var description))
        {
            if (description.ValueKind == JsonValueKind.Null)
                patch = patch with { SetDescription = true };
            else if (description.ValueKind == JsonValueKind.String && description.GetString()!.Length <= 4000)
                patch = patch with { SetDescription = true, Description = description.GetString() };
            else
                errors["description"] = ["Must be null or at most 4000 characters."];
        }
        return (patch, errors);
    }

    /// <summary>
    /// <c>PATCH /api/games/{id}/rating</c> body: <c>{rating?: 1–5 | null, favourite?: bool}</c>, at least one present;
    /// an absent property is unchanged, <c>rating: null</c> clears it.
    /// </summary>
    public static (GamePatch Patch, Dictionary<string, string[]> Errors) ParseRating(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        var patch = new GamePatch();
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors[""] = ["A JSON object is required."];
            return (patch, errors);
        }

        var hasRating = body.TryGetProperty("rating", out var rating);
        var hasFavourite = body.TryGetProperty("favourite", out var favourite);
        if (!hasRating && !hasFavourite)
            errors[""] = ["Give rating, favourite or both."];

        if (hasRating)
        {
            if (rating.ValueKind == JsonValueKind.Null)
                patch = patch with { SetRating = true };
            else if (rating.ValueKind == JsonValueKind.Number && rating.TryGetInt32(out var value) && value is >= 1 and <= 5)
                patch = patch with { SetRating = true, Rating = value };
            else
                errors["rating"] = ["Must be null or a whole number 1–5."];
        }

        if (hasFavourite)
        {
            if (favourite.ValueKind is JsonValueKind.True or JsonValueKind.False)
                patch = patch with { Favourite = favourite.GetBoolean() };
            else
                errors["favourite"] = ["Must be true or false."];
        }
        return (patch, errors);
    }

    private static List<string>? ParseList(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var list))
            return null;
        if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String))
        {
            errors[field] = ["Must be an array of strings."];
            return null;
        }
        var items = list.EnumerateArray().Select(i => i.GetString()!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (items.Count > 20 || items.Any(i => i.Length is < 1 or > 255))
            errors[field] = ["At most 20 items of 1–255 characters each."];
        return items;
    }

    public static (GameQuery Query, Dictionary<string, string[]> Errors) ParseQuery(string? system, string? letter, string? region,
        string? q, string? sort, string? dir, int? page, int? pageSize, ISystemRegistry systems, string? favourite = null,
        string? collection = null)
    {
        var errors = Paging.Validate(page, pageSize);
        if (system is not null && systems.ById(system) is null)
            errors["system"] = ["Choose a registered system."];

        if (letter is not null)
        {
            letter = letter.ToUpperInvariant();
            if (letter is not ("#" or [>= 'A' and <= 'Z']))
                errors["letter"] = ["Must be # or a letter A–Z."];
        }

        region = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (q?.Length > 255)
            errors["q"] = ["Must be at most 255 characters."];
        else if (new GameQuery { Search = q }.Words.Count > GameQuery.MaxWords)
            errors["q"] = [$"At most {GameQuery.MaxWords} words."];

        GameSort gameSort = GameSort.Title;
        switch (sort)
        {
            case null or "title": break;
            case "year": gameSort = GameSort.Year; break;
            case "added": gameSort = GameSort.Added; break;
            case "rating": gameSort = GameSort.Rating; break;
            case "relevance": gameSort = GameSort.Relevance; break;
            default: errors["sort"] = ["Must be title, year, added, rating or relevance."]; break;
        }
        if (dir is not (null or "asc" or "desc"))
            errors["dir"] = ["Must be asc or desc."];

        bool? onlyFavourite = null;
        switch (favourite)
        {
            case null: break;
            case "true": onlyFavourite = true; break;
            case "false": onlyFavourite = false; break;
            default: errors["favourite"] = ["Must be true or false."]; break;
        }
        Guid? collectionId = null;
        if (collection is not null)
        {
            if (Guid.TryParse(collection, out var parsedCollection))
                collectionId = parsedCollection;
            else
                errors["collection"] = ["Must be a collection id."];
        }
        var query = new GameQuery
        {
            System = system,
            Letter = letter,
            Region = region,
            Search = q,
            Favourite = onlyFavourite,
            CollectionId = collectionId,
            Sort = gameSort,
            // §23: asc for title/year, desc for added (to the library, or to the collection: B15) and rating; relevance ignores it.
            Descending = gameSort != GameSort.Relevance && (dir is null ? gameSort is GameSort.Added or GameSort.Rating : dir == "desc"),
            Page = page ?? 1,
            PageSize = pageSize ?? Page<GameListItem>.DefaultSize,
        };
        return (query, errors);
    }

    internal static string ImageUrl(Guid gameId, long imageId) => $"/api/games/{gameId}/images/{imageId}";

    private static object ToDetail(GameRow game, string systemName, IReadOnlyList<RomRow> roms, IReadOnlyList<ImageRow> images,
        IReadOnlySet<string> notes) => new
    {
        id = game.Id,
        system = game.SystemId,
        systemName,
        title = game.Title,
        displayTitle = game.DisplayTitle,
        releaseYear = game.ReleaseYear,
        developers = game.Developers,
        publishers = game.Publishers,
        genres = game.Genres,
        description = game.Description,
        hasDefaultBoxArt = game.HasDefaultBoxArt,
        rating = game.Rating,
        favourite = game.Favourite,
        metadata = new { createdAt = game.CreatedAt, updatedAt = game.UpdatedAt },
        roms = roms.OrderByDescending(r => r.IsPrimary).ThenBy(r => r.FileName, StringComparer.Ordinal).Select(r => new
        {
            id = r.Id,
            fileName = r.FileName,
            originalFileName = r.OriginalFileName,
            baseTitle = r.BaseTitle,
            regions = r.Regions,
            languages = r.Languages,
            revision = r.Revision,
            flags = r.Flags,
            isPrimary = r.IsPrimary,
            identificationMethod = r.IdMethod,
            datName = r.DatName,
            datVersion = r.DatVersion,
            datGameName = r.DatGameName,
            header = r.Header is null ? null : JsonNode.Parse(r.Header),
            sizeBytes = r.SizeBytes,
            crc32 = r.Crc32,
            md5 = r.Md5,
            sha1 = r.Sha1,
            dateAdded = r.DateAdded,
            dateModified = r.DateModified,
            downloadUrl = $"/api/games/{game.Id}/roms/{r.Id}/download",
            hasNote = notes.Contains(LibraryZip.NoteEntry(r.FileName)),
        }),
        images = images.Select(i => new
        {
            id = i.Id,
            type = i.Type,
            fileName = i.FileName,
            source = i.Source,
            isDefault = i.IsDefault,
            isCover = i.IsCover,
            sizeBytes = i.SizeBytes,
            width = i.Width,
            height = i.Height,
            url = ImageUrl(game.Id, i.Id),
        }),
    };
}

/// <summary>U1 body: <c>{ "url": "…", "type": "BoxArt" | "Snap" }</c>.</summary>
public sealed record ImageUrlRequest(string? Url, string? Type);
