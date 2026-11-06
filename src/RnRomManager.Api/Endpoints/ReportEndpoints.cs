using RnRomManager.Core.Database;
using RnRomManager.Core.Reports;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>/api/reports</c>: the Reporting page (RP1–RP8). Any signed-in user can read.</summary>
public static class ReportEndpoints
{
    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("/reports");

        reports.MapGet("/missing-images", async (string? system, int? page, int? pageSize, HttpContext ctx,
            IReportRepository repository, ISystemRegistry systems) =>
        {
            if (Validate(system, page, pageSize, systems) is { Count: > 0 } errors)
                return Problems.Validation(ctx, errors);
            var result = await repository.ListMissingImagesAsync(system, page ?? 1, pageSize ?? 50, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, item => new
            {
                id = item.Id, displayTitle = item.DisplayTitle, system = item.SystemId,
                systemName = SystemName(systems, item.SystemId), reason = item.Reason,
            }));
        });

        reports.MapGet("/library-size", async (IReportRepository repository, ISystemRegistry systems, CancellationToken cancellationToken) =>
        {
            var sizes = await repository.ListSystemSizesAsync(cancellationToken);
            return Results.Ok(new
            {
                totalBytes = sizes.Sum(s => s.Bytes),
                gameCount = sizes.Sum(s => s.GameCount),
                systems = sizes.Select(s => new
                {
                    system = s.SystemId, systemName = SystemName(systems, s.SystemId), bytes = s.Bytes, gameCount = s.GameCount,
                }),
            });
        });

        reports.MapGet("/library-size/{system}", async (string system, HttpContext ctx, IReportRepository repository,
            ISystemRegistry systems) =>
        {
            if (systems.ById(system) is null)
                return Problems.NotFound(ctx);
            var games = await repository.ListGameSizesAsync(system, ctx.RequestAborted);
            return Results.Ok(new
            {
                system, systemName = SystemName(systems, system), bytes = games.Sum(g => g.Bytes),
                games = games.Select(g => new { id = g.Id, displayTitle = g.DisplayTitle, bytes = g.Bytes, romCount = g.RomCount }),
            });
        });

        return api;
    }

    /// <summary>The paged reports' shared rules: house paging, and an optional registered <c>system</c>.</summary>
    private static Dictionary<string, string[]> Validate(string? system, int? page, int? pageSize, ISystemRegistry systems)
    {
        var errors = Paging.Validate(page, pageSize);
        if (system is not null && systems.ById(system) is null)
            errors["system"] = ["Choose a registered system."];
        return errors;
    }

    private static string SystemName(ISystemRegistry systems, string id) => systems.ById(id)?.DisplayName ?? id;
}
