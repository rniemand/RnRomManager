using RnRomManager.Core.Database;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>/api/discovery/{kind}*</c> (collections discovery B3): any signed-in user browses the publisher and genre
/// indexes across systems. <c>kind</c> is exactly <c>publishers</c> or <c>genres</c>; anything else, and a group no
/// game has, answers 404.
/// </summary>
public static class DiscoveryEndpoints
{
    public static RouteGroupBuilder MapDiscoveryEndpoints(this RouteGroupBuilder api)
    {
        var discovery = api.MapGroup("/discovery");

        discovery.MapGet("/{kind}", async (string kind, int? page, int? pageSize, HttpContext ctx, IDiscoveryRepository repository,
            IPublisherLogoRepository logos) =>
        {
            if (!DiscoveryKinds.TryParse(kind, out var parsed))
                return Problems.NotFound(ctx);
            var errors = Paging.Validate(page, pageSize);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var result = await repository.ListGroupsAsync(parsed, page ?? 1, pageSize ?? Page<DiscoveryGroup>.DefaultSize, ctx.RequestAborted);
            if (parsed != DiscoveryKind.Publishers)
                return Results.Ok(Paging.ToDto(result, g => new { key = g.Key, name = g.Name, gameCount = g.GameCount }));

            // B6: only the override's URL; the SPA falls back to its starter art, then text.
            var updated = await logos.ListUpdatedAtAsync(result.Items.Select(g => g.Key).ToList(), ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, g => new
            {
                key = g.Key,
                name = g.Name,
                gameCount = g.GameCount,
                logoUrl = updated.TryGetValue(g.Key, out var at) ? PublisherLogoEndpoints.LogoUrl(g.Key, at) : null,
            }));
        });

        discovery.MapGet("/{kind}/games", async (string kind, string? key, int? page, int? pageSize, HttpContext ctx,
            IDiscoveryRepository repository, ISystemRegistry systems, IGameRepository games) =>
        {
            if (!DiscoveryKinds.TryParse(kind, out var parsed))
                return Problems.NotFound(ctx);
            var errors = Paging.Validate(page, pageSize);
            if (!DiscoveryKey.IsValid(key))
                errors["key"] = ["Must be 64 lowercase hexadecimal characters."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var result = await repository.ListGamesAsync(
                parsed, key!, page ?? 1, pageSize ?? Page<GameListItem>.DefaultSize, ctx.RequestAborted);
            return result is null
                ? Problems.NotFound(ctx)
                : Results.Ok(Paging.ToDto(await LibraryEndpoints.ForViewerAsync(result, ctx, games), game => LibraryEndpoints.ToListItemDto(game, systems)));
        });

        return api;
    }
}
