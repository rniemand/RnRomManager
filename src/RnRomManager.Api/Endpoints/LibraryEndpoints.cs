using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// Library reads for every signed-in user (spec §23 <c>/api/systems</c>, <c>/api/games*</c>): lists and details come from
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

        games.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, IGameRepository repository, IRomRepository roms, IImageRepository images) =>
        {
            if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            var romRows = await roms.ListByGameAsync(id, ctx.RequestAborted);
            var imageRows = await images.ListByGameAsync(id, ctx.RequestAborted);
            return Results.Ok(ToDetail(game, romRows, imageRows));
        });

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

        return api;
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
