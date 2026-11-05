using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Database;
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
            return Results.Ok(systems.All.Select(handler => stats.TryGetValue(handler.Id, out var s)
                ? new { id = handler.Id, displayName = handler.DisplayName, gameCount = s.GameCount, romCount = s.RomCount, regions = s.Regions }
                : new { id = handler.Id, displayName = handler.DisplayName, gameCount = 0, romCount = 0, regions = (IReadOnlyList<string>)[] }));
        });

        var games = api.MapGroup("/games");

        games.MapGet("", async (string? system, string? letter, string? region, string? q, string? sort, string? dir, int? page,
            int? pageSize, HttpContext ctx, ISystemRegistry systems, IGameRepository repository) =>
        {
            var (query, errors) = ParseQuery(system, letter, region, q, sort, dir, page, pageSize, systems);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await repository.ListAsync(query, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, game => new
            {
                id = game.Id,
                system = game.SystemId,
                displayTitle = game.DisplayTitle,
                releaseYear = game.ReleaseYear,
                regions = game.Regions,
                romCount = game.RomCount,
                boxArtUrl = game.BoxArtImageId is { } imageId ? ImageUrl(game.Id, imageId) : null,
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
            return files.OpenEntry(ctx, game, LibraryZip.ImageEntry(image.FileName), PngInfo.MaxBytes, out var stream) is { } problem
                ? problem
                : Results.Stream(stream!, "image/png", entityTag: new EntityTagHeaderValue($"\"{image.Sha1}\""));
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

        // Admin edits (§11, D28): each takes the job lock inside LibraryEditor.
        games.MapPatch("/{id:guid}", async (Guid id, JsonElement body, HttpContext ctx, LibraryEditor editor, IGameRepository repository,
            IRomRepository roms, IImageRepository images) =>
        {
            var (patch, errors) = ParsePatch(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.PatchAsync(id, patch, ctx.RequestAborted);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, LibraryEditor editor) =>
            ToResult(ctx, await editor.DeleteGameAsync(id, ctx.RequestAborted))).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapDelete("/{id:guid}/roms/{romId:long}", async (Guid id, long romId, HttpContext ctx, LibraryEditor editor) =>
            ToResult(ctx, await editor.DeleteReleaseAsync(id, romId, ctx.RequestAborted))).RequireAuthorization(AuthSetup.AdminOnly);

        return api;
    }

    private static async Task<IResult> DetailAsync(Guid id, HttpContext ctx, IGameRepository repository, IRomRepository roms, IImageRepository images)
    {
        if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
            return Problems.NotFound(ctx);
        var romRows = await roms.ListByGameAsync(id, ctx.RequestAborted);
        var imageRows = await images.ListByGameAsync(id, ctx.RequestAborted);
        return Results.Ok(ToDetail(game, romRows, imageRows));
    }

    private static IResult ToResult(HttpContext ctx, LibraryEditResult result) =>
        result.Code is null ? Results.NoContent() : ToProblem(ctx, result);

    /// <summary>§23 mutation bullet: 409 for the lock and unwritable ZIPs, 404 for a stale id, 500 for write failures.</summary>
    private static IResult ToProblem(HttpContext ctx, LibraryEditResult result) => result.Code switch
    {
        LibraryEditResult.NotFound => Problems.NotFound(ctx),
        LibraryEditResult.JobRunning or LibraryTarget.InfoJsonNewerSchema or LibraryTarget.TargetZipUnreadable =>
            Problems.Create(ctx, StatusCodes.Status409Conflict, result.Code, result.Code),
        _ => Problems.Create(ctx, StatusCodes.Status500InternalServerError, result.Code!, result.Code!),
    };

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
            if (value is not { Length: >= 1 and <= 255 } || FileNameSanitizer.Sanitize(value).Length == 0)
                errors["title"] = ["Must contain 1–255 characters and produce a nonempty filename."];
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
        string? q, string? sort, string? dir, int? page, int? pageSize, ISystemRegistry systems)
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

        GameSort gameSort = GameSort.Title;
        switch (sort)
        {
            case null or "title": break;
            case "year": gameSort = GameSort.Year; break;
            case "added": gameSort = GameSort.Added; break;
            default: errors["sort"] = ["Must be title, year or added."]; break;
        }
        if (dir is not (null or "asc" or "desc"))
            errors["dir"] = ["Must be asc or desc."];

        var query = new GameQuery
        {
            System = system,
            Letter = letter,
            Region = region,
            Search = q,
            Sort = gameSort,
            // §23: asc for title/year, desc for added.
            Descending = dir is null ? gameSort == GameSort.Added : dir == "desc",
            Page = page ?? 1,
            PageSize = pageSize ?? Page<GameListItem>.DefaultSize,
        };
        return (query, errors);
    }

    private static string ImageUrl(Guid gameId, long imageId) => $"/api/games/{gameId}/images/{imageId}";

    private static object ToDetail(GameRow game, IReadOnlyList<RomRow> roms, IReadOnlyList<ImageRow> images) => new
    {
        id = game.Id,
        system = game.SystemId,
        title = game.Title,
        displayTitle = game.DisplayTitle,
        releaseYear = game.ReleaseYear,
        developers = game.Developers,
        publishers = game.Publishers,
        genres = game.Genres,
        description = game.Description,
        hasDefaultBoxArt = game.HasDefaultBoxArt,
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
        }),
        images = images.Select(i => new
        {
            id = i.Id,
            type = i.Type,
            isDefault = i.IsDefault,
            width = i.Width,
            height = i.Height,
            url = ImageUrl(game.Id, i.Id),
        }),
    };
}
