using System.Text.RegularExpressions;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Database;
using RnRomManager.Core.Library;
using RnRomManager.Core.Reports;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>/api/reports</c>: the Reporting page (RP1–RP8). Any signed-in user can read.</summary>
public static partial class ReportEndpoints
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

        reports.MapGet("/duplicates", async (string? system, int? page, int? pageSize, HttpContext ctx, IReportRepository repository,
            ISystemRegistry systems) =>
        {
            if (Validate(system, page, pageSize, systems) is { Count: > 0 } errors)
                return Problems.Validation(ctx, errors);
            var result = await repository.ListDuplicatesAsync(system, page ?? 1, pageSize ?? 25, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, group => new
            {
                system = group.SystemId, systemName = SystemName(systems, group.SystemId), baseTitle = group.BaseTitle,
                gamesKey = group.GamesKey,
                games = group.Games.Select(g => new
                {
                    id = g.Id, displayTitle = g.DisplayTitle, romCount = g.RomCount, zipSizeBytes = g.ZipSizeBytes, createdAt = g.CreatedAt,
                    boxArtUrl = g.BoxArtImageId is { } imageId ? LibraryEndpoints.ImageUrl(g.Id, imageId) : null,
                }),
            }));
        });

        reports.MapPost("/duplicates/dismiss", async (DismissDuplicateRequest request, HttpContext ctx, IReportRepository repository,
            ISystemRegistry systems, TimeProvider time) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrEmpty(request.System) || systems.ById(request.System) is null)
                errors["system"] = ["Choose a registered system."];
            if (string.IsNullOrEmpty(request.BaseTitle) || request.BaseTitle.Length > 255)
                errors["baseTitle"] = ["Must contain 1–255 characters."];
            if (request.GamesKey is null || !GamesKeyPattern().IsMatch(request.GamesKey))
                errors["gamesKey"] = ["Must be 64 lower-case hex characters."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            // A key that matches no current group is stored anyway: the group may have just changed (RP8).
            await repository.DismissDuplicateAsync(request.System!, request.BaseTitle!, request.GamesKey!, DatEndpoints.UserId(ctx.User),
                time.GetUtcNow().UtcDateTime, ctx.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization(AuthSetup.LibraryEditor);

        reports.MapPost("/duplicates/merge", async (MergeDuplicatesRequest request, HttpContext ctx, LibraryEditor editor) =>
        {
            var sources = request.SourceGameIds ?? [];
            var errors = new Dictionary<string, string[]>();
            if (request.TargetGameId is not { } target || target == Guid.Empty)
                errors["targetGameId"] = ["A target game is required."];
            if (sources.Count is < 1 or > MaxMergeSources || sources.Contains(Guid.Empty))
                errors["sourceGameIds"] = [$"Give 1–{MaxMergeSources} source games."];
            else if (sources.Distinct().Count() != sources.Count || sources.Contains(request.TargetGameId ?? Guid.Empty))
                errors["sourceGameIds"] = ["Sources must be distinct and must not include the target."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            // One merge per source, each under the job lock like Merge into…; the first failure stops the loop.
            var merged = new List<Guid>();
            foreach (var source in sources)
            {
                var result = await editor.MergeAsync(source, request.TargetGameId!.Value, ctx.RequestAborted);
                if (result.Code is null)
                {
                    merged.Add(source);
                    continue;
                }
                // DbWriteFailed: the target ZIP was written and the source ZIP deleted, so its files did move.
                if (result.Code == LibraryEditResult.DbWriteFailed)
                    merged.Add(source);
                return LibraryEndpoints.ToProblem(ctx, result, new Dictionary<string, object?> { ["merged"] = merged });
            }
            return Results.Ok(new { targetGameId = request.TargetGameId });
        }).RequireAuthorization(AuthSetup.LibraryEditor);

        return api;
    }

    private const int MaxMergeSources = 20;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex GamesKeyPattern();

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

/// <summary><c>POST /api/reports/duplicates/dismiss</c> (RP8).</summary>
public sealed record DismissDuplicateRequest(string? System, string? BaseTitle, string? GamesKey);

/// <summary><c>POST /api/reports/duplicates/merge</c> (RP8): every source is merged into the target, in order.</summary>
public sealed record MergeDuplicatesRequest(Guid? TargetGameId, IReadOnlyList<Guid>? SourceGameIds);
