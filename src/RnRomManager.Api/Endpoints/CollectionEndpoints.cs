using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RnRomManager.Api.Auth;
using RnRomManager.Api.Http;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>/api/collections*</c> (B1–B7, B13–B14): any signed-in user, on their own collections only; another user's id
/// answers 404 like a missing one.
/// </summary>
public static class CollectionEndpoints
{
    public sealed record CreateCollectionRequest(string? Name, string? Notes);

    public sealed record AddGamesRequest(IReadOnlyList<Guid>? GameIds);

    public sealed record CoverUrlRequest(string? Url);

    public static RouteGroupBuilder MapCollectionEndpoints(this RouteGroupBuilder api)
    {
        var collections = api.MapGroup("/collections");

        collections.MapGet("", async (string? q, int? page, int? pageSize, HttpContext ctx, ICollectionRepository repository) =>
        {
            var errors = Paging.Validate(page, pageSize);
            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            var words = new GameQuery { Search = q }.Words;
            if (q?.Length > 255)
                errors["q"] = ["Must be at most 255 characters."];
            else if (words.Count > GameQuery.MaxWords)
                errors["q"] = [$"At most {GameQuery.MaxWords} words."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var result = await repository.ListAsync(UserId(ctx), words, page ?? 1, pageSize ?? Page<Collection>.DefaultSize, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, ToListDto));
        });

        collections.MapPost("", async (CreateCollectionRequest? request, HttpContext ctx, CollectionService service) =>
            ToResult(ctx, await service.CreateAsync(UserId(ctx), request?.Name, request?.Notes, ctx.RequestAborted),
                r => Results.Created($"/api/collections/{r.Collection!.Id}", ToDto(r.Collection))));

        collections.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, CollectionService service) =>
            await service.GetAsync(id, UserId(ctx), ctx.RequestAborted) is { } collection ? Results.Ok(ToDto(collection)) : Problems.NotFound(ctx));

        collections.MapPatch("/{id:guid}", async (Guid id, JsonElement body, HttpContext ctx, CollectionService service) =>
        {
            var (patch, errors) = ParsePatch(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await service.UpdateAsync(id, UserId(ctx), patch, ctx.RequestAborted);
            // The detail carries gameCount, which the update itself doesn't read.
            return await DetailResultAsync(ctx, result, service, id);
        });

        collections.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, CollectionService service) =>
            ToResult(ctx, await service.DeleteAsync(id, UserId(ctx), ctx.RequestAborted), _ => Results.NoContent()));

        collections.MapPost("/{id:guid}/games", async (Guid id, AddGamesRequest? request, HttpContext ctx, CollectionService service) =>
            ToResult(ctx, await service.AddGamesAsync(id, UserId(ctx), request?.GameIds, ctx.RequestAborted),
                r => Results.Ok(new { added = r.Added })));

        collections.MapDelete("/{id:guid}/games/{gameId:guid}", async (Guid id, Guid gameId, HttpContext ctx, CollectionService service) =>
            ToResult(ctx, await service.RemoveGameAsync(id, UserId(ctx), gameId, ctx.RequestAborted), _ => Results.NoContent()));

        // B8: same form handling and limits as the game image upload.
        collections.MapPost("/{id:guid}/cover", async (Guid id, HttpContext ctx, CollectionService service) =>
        {
            var (form, problem) = await LibraryEndpoints.ReadImageFormAsync(ctx);
            if (problem is not null)
                return problem;
            var errors = new Dictionary<string, string[]>();
            var bytes = await LibraryEndpoints.ReadImageFileAsync(form!, errors, ctx.RequestAborted);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            return await DetailResultAsync(ctx, await service.SetCoverAsync(id, UserId(ctx), bytes!, ctx.RequestAborted), service, id);
        }).WithMetadata(new RequestSizeLimitAttribute(LibraryEndpoints.UploadRequestLimit));

        // B9: open to plain users on purpose; the downloader only reaches public addresses.
        collections.MapPost("/{id:guid}/cover/from-url", async (Guid id, CoverUrlRequest? request, HttpContext ctx,
            ImageUrlDownloader downloader, CollectionService service) =>
        {
            if (LibraryEndpoints.ParseImageUrl(request?.Url) is not { } url)
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["url"] = ["Enter an http or https image URL."] });
            if (await service.GetAsync(id, UserId(ctx), ctx.RequestAborted) is null)
                return Problems.NotFound(ctx);
            var download = await downloader.DownloadAsync(url, ctx.RequestAborted);
            if (download.Bytes is not { } bytes)
                return Problems.Create(ctx, download.Status, download.Code!, download.Title!);
            if (LibraryEndpoints.DownloadedImageError(bytes) is { } bytesError)
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["url"] = [bytesError] });
            return await DetailResultAsync(ctx, await service.SetCoverAsync(id, UserId(ctx), bytes, ctx.RequestAborted), service, id);
        });

        // B11: the row's format decides which file is served; a missing file (e.g. after a restore) is 404.
        collections.MapGet("/{id:guid}/cover", async (Guid id, HttpContext ctx, CollectionService service, CollectionCovers covers) =>
        {
            if (await service.GetAsync(id, UserId(ctx), ctx.RequestAborted) is not { CoverFormat: { } format }
                || covers.Open(id, format) is not { } stream)
                return Problems.NotFound(ctx);
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.Stream(stream, ImageFormats.ContentType($"cover.{format}"));
        });

        collections.MapDelete("/{id:guid}/cover", async (Guid id, HttpContext ctx, CollectionService service) =>
            await DetailResultAsync(ctx, await service.RemoveCoverAsync(id, UserId(ctx), ctx.RequestAborted), service, id));

        return api;
    }

    /// <summary>The signed-in user's id; every route here runs behind the authenticated policy.</summary>
    internal static int UserId(HttpContext ctx) => AuditActor.From(ctx.User).UserId ?? throw new InvalidOperationException("No user id claim.");

    internal static IResult ToResult(HttpContext ctx, CollectionResult result, Func<CollectionResult, IResult> ok) => result.Error switch
    {
        CollectionResult.Failure.None => ok(result),
        CollectionResult.Failure.NotFound => Problems.NotFound(ctx),
        CollectionResult.Failure.ValidationFailed => Problems.Validation(ctx, result.Errors!),
        CollectionResult.Failure.NameTaken => Problems.Create(ctx, StatusCodes.Status409Conflict, "CollectionNameTaken",
            "You already have a collection with this name."),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    /// <summary>A successful edit answers with the fresh detail (B4, B8, B11).</summary>
    internal static async Task<IResult> DetailResultAsync(HttpContext ctx, CollectionResult result, CollectionService service, Guid id)
    {
        if (result.Error != CollectionResult.Failure.None)
            return ToResult(ctx, result, _ => Results.Empty);
        return await service.GetAsync(id, UserId(ctx), ctx.RequestAborted) is { } fresh ? Results.Ok(ToDto(fresh)) : Problems.NotFound(ctx);
    }

    /// <summary>
    /// <c>PATCH /api/collections/{id}</c> body (B4): an absent property is unchanged; <c>name</c> must be a string;
    /// <c>notes</c> may be a string or null (null or blank clears it).
    /// </summary>
    public static (CollectionPatch Patch, Dictionary<string, string[]> Errors) ParsePatch(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors[""] = ["A JSON object is required."];
            return (new CollectionPatch(), errors);
        }

        string? name = null;
        if (body.TryGetProperty("name", out var nameValue))
        {
            if (nameValue.ValueKind == JsonValueKind.String)
                name = nameValue.GetString();
            else
                errors["name"] = ["A name is required."];
        }

        var notesSet = body.TryGetProperty("notes", out var notesValue);
        string? notes = null;
        if (notesSet)
        {
            if (notesValue.ValueKind == JsonValueKind.String)
                notes = notesValue.GetString();
            else if (notesValue.ValueKind != JsonValueKind.Null)
                errors["notes"] = ["Notes must be a string or null."];
        }

        return (new CollectionPatch(name, notesSet, notes), errors);
    }

    internal static string? CoverUrl(Collection c) =>
        c.CoverUpdatedAt is { } at ? $"/api/collections/{c.Id}/cover?v={at.Ticks}" : null;

    internal static object ToDto(Collection c) => new
    {
        id = c.Id,
        name = c.Name,
        notes = c.Notes,
        coverUrl = CoverUrl(c),
        gameCount = c.GameCount,
        createdAt = c.CreatedAt,
        updatedAt = c.UpdatedAt,
    };

    private static object ToListDto(Collection c) => new
    {
        id = c.Id,
        name = c.Name,
        gameCount = c.GameCount,
        coverUrl = CoverUrl(c),
        updatedAt = c.UpdatedAt,
    };
}
