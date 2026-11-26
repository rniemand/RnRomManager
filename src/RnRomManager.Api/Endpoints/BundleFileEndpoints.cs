using Microsoft.AspNetCore.Mvc;
using RnRomManager.Core.Bundles;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>POST /api/bundle-files/resolve</c> (Bundle Creator B5): any signed-in user; state-free. Reports whether each
/// identity names a file in today's catalog, with its current names and normal download URL. Opening the file can still fail.
/// </summary>
public static class BundleFileEndpoints
{
    public static RouteGroupBuilder MapBundleFileEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/bundle-files/resolve", async (HttpContext ctx, IRomRepository roms) =>
        {
            var (request, problem) = await BundleQueueEndpoints.ReadJsonAsync<ResolveRequest>(ctx);
            if (problem is not null)
                return problem;
            var (identities, errors) = Validate(request!);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var targets = await roms.ResolveBundleFilesAsync(identities, ctx.RequestAborted);
            return Results.Ok(new
            {
                items = targets.Select(t => new
                {
                    available = t is not null,
                    gameTitle = t?.GameTitle,
                    fileName = t?.FileName,
                    downloadUrl = t?.DownloadUrl,
                }),
            });
        }).WithMetadata(new RequestSizeLimitAttribute(BundleQueueEndpoints.RequestLimit));

        return api;
    }

    /// <summary>The parsed identities in order (duplicates allowed), or field errors keyed <c>items[i].…</c>.</summary>
    internal static (IReadOnlyList<BundleFileIdentity> Identities, Dictionary<string, string[]> Errors) Validate(ResolveRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Items is not { } items)
        {
            errors["items"] = ["An array of items is required."];
            return ([], errors);
        }
        if (items.Count > BundleFile.MaxBatch)
        {
            errors["items"] = [$"At most {BundleFile.MaxBatch} items."];
            return ([], errors);
        }

        var identities = new List<BundleFileIdentity>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not { } item)
                errors[$"items[{i}]"] = ["An item is required."];
            else if (BundleQueueValidation.ParseIdentity(item.GameId, item.ReleaseSha1, item.Member, out var identity) is { } error)
                errors[$"items[{i}].{error.Field}"] = [error.Message];
            else
                identities.Add(identity!);
        }
        return (identities, errors);
    }
}
