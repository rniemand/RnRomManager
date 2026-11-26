using RnRomManager.Core.Database;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>/api/discovery/{kind}*</c> (collections discovery B3): any signed-in user browses the publisher, developer and
/// genre indexes across systems. <c>kind</c> is exactly <c>publishers</c>, <c>developers</c> or <c>genres</c>; anything
/// else, and a group no game has, answers 404.
/// </summary>
public static class DiscoveryEndpoints
{
    public static RouteGroupBuilder MapDiscoveryEndpoints(this RouteGroupBuilder api)
    {
        var discovery = api.MapGroup("/discovery");

        discovery.MapGet("/{kind}", async (string kind, int? page, int? pageSize, string? q, string? sort, string? dir, HttpContext ctx,
            IDiscoveryRepository repository, IDiscoveryLogoRepository logos) =>
        {
            if (!DiscoveryKinds.TryParse(kind, out var parsed))
                return Problems.NotFound(ctx);
            var errors = Paging.Validate(page, pageSize);
            // B16: a label filter, trimmed and capped like Review's q.
            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            if (q?.Length > 100)
                errors["q"] = ["Must be at most 100 characters."];
            DiscoveryGroupSort? sortKey = sort?.ToLowerInvariant() switch
            {
                null or "name" => DiscoveryGroupSort.Name,
                "games" => DiscoveryGroupSort.Games,
                _ => null,
            };
            if (sortKey is null)
                errors["sort"] = ["Must be name or games."];
            var descending = SortQuery.ParseDir(dir, errors, whenMissing: null);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            // A1: a missing dir is the sort's own default, as multiple-releases does.
            descending ??= sortKey == DiscoveryGroupSort.Games;
            var result = await repository.ListGroupsAsync(parsed, page ?? 1, pageSize ?? Page<DiscoveryGroup>.DefaultSize, q, sortKey!.Value,
                descending.Value, ctx.RequestAborted);
            // B6, B15: only the shared logo's URL; the SPA falls back to its starter art, then a text banner.
            var logoKind = parsed switch
            {
                DiscoveryKind.Publishers => LogoKind.Publishers,
                DiscoveryKind.Developers => LogoKind.Developers,
                _ => LogoKind.Genres,
            };
            var updated = await logos.ListUpdatedAtAsync(logoKind, result.Items.Select(g => g.Key).ToList(), ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, g => new
            {
                key = g.Key,
                name = g.Name,
                gameCount = g.GameCount,
                logoUrl = updated.TryGetValue(g.Key, out var at) ? DiscoveryLogoEndpoints.LogoUrl(logoKind, g.Key, at) : null,
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
                : Results.Ok(Paging.ToDto(await LibraryEndpoints.ForViewerAsync(result, ctx, games), game => LibraryDtos.ToListItemDto(game, systems)));
        });

        // P4: a metadata value's group key, so the game page can link straight to the group.
        discovery.MapGet("/{kind}/key", async (string kind, string? name, HttpContext ctx, IDiscoveryRepository repository) =>
        {
            if (!DiscoveryKinds.TryParse(kind, out var parsed))
                return Problems.NotFound(ctx);
            if (name?.Trim() is not { Length: >= 1 and <= 255 })
                return Problems.Validation(ctx, "name", "Must be 1–255 characters.");
            return await repository.FindKeyAsync(parsed, name, ctx.RequestAborted) is { } key
                ? Results.Ok(new { key })
                : Problems.NotFound(ctx);
        });

        return api;
    }
}
