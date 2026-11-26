using RnRomManager.Core.Database;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>/api/timeline*</c> (Timeline T8, T9): any signed-in user browses the games linked to IGDB that have a release year,
/// counted per year and listed by year range; and <c>/api/igdb/games/{igdbId}/page</c> (T7), a redirect to a game's IGDB page.
/// </summary>
public static class TimelineEndpoints
{
    /// <summary>T6: the Timeline table shows 50 games a page.</summary>
    public const int DefaultPageSize = 50;

    public static RouteGroupBuilder MapTimelineEndpoints(this RouteGroupBuilder api)
    {
        var timeline = api.MapGroup("/timeline");

        timeline.MapGet("", async (string? system, HttpContext ctx, ISystemRegistry systems, IGameRepository games, IGameInfoLookup lookup) =>
        {
            if (system is not null && systems.ById(system) is null)
                return Problems.Validation(ctx, "system", "Choose a registered system.");
            var years = await games.ListReleaseYearCountsAsync(system, ctx.RequestAborted);
            return Results.Ok(new
            {
                igdbConfigured = lookup.Configured,
                total = years.Sum(y => y.Count),
                years = years.Select(y => new { year = y.Year, count = y.Count }),
            });
        });

        timeline.MapGet("/games", async (string? system, int? from, int? to, int? page, int? pageSize, HttpContext ctx,
            ISystemRegistry systems, IGameRepository games) =>
        {
            var errors = Paging.Validate(page, pageSize);
            if (system is not null && systems.ById(system) is null)
                errors["system"] = ["Choose a registered system."];
            if (from is < 1 or > 9999)
                errors["from"] = ["Must be 1–9999."];
            if (to is < 1 or > 9999)
                errors["to"] = ["Must be 1–9999."];
            if (from > to && !errors.ContainsKey("from") && !errors.ContainsKey("to"))
                errors["from"] = ["Must not be after to."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var query = new GameQuery
            {
                System = system,
                IgdbLinked = true,
                // T2: the bounds are always set, so games without a year stay out.
                YearFrom = from ?? 1,
                YearTo = to ?? 9999,
                Sort = GameSort.Year,
                Page = page ?? 1,
                PageSize = pageSize ?? DefaultPageSize,
            };
            var result = await LibraryEndpoints.ForViewerAsync(await games.ListAsync(query, ctx.RequestAborted), ctx, games);
            return Results.Ok(Paging.ToDto(result, game => LibraryDtos.ToListItemDto(game, systems)));
        });

        // T7: any signed-in user; found pages and not-found results are cached here, failures aren't.
        api.MapGet("/igdb/games/{igdbId:int}/page", async (int igdbId, HttpContext ctx, IGameInfoLookup lookup, IgdbPageUrlCache cache) =>
        {
            if (igdbId < 1)
                return Problems.NotFound(ctx);
            if (!lookup.Configured)
                return Problems.Create(ctx, StatusCodes.Status409Conflict, LibraryEndpoints.IgdbNotConfigured,
                    "Metadata lookup needs Sources:IgdbClientId and Sources:IgdbClientSecret.");
            if (!cache.TryGet(igdbId, out var url))
            {
                var result = await lookup.GamePageUrlAsync(igdbId, ctx.RequestAborted);
                if (result.FailedTitle is not null)
                    return Problems.Create(ctx, StatusCodes.Status502BadGateway, LibraryEndpoints.IgdbFailed, result.FailedTitle);
                cache.Set(igdbId, url = result.Url);
            }
            return url is null ? Problems.NotFound(ctx) : Results.Redirect(url);
        });

        return api;
    }
}
