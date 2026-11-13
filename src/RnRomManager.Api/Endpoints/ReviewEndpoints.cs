using System.Text.Json.Nodes;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Ingest;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

public static class ReviewEndpoints
{
    public static RouteGroupBuilder MapReviewEndpoints(this RouteGroupBuilder api)
    {
        var review = api.MapGroup("/review").RequireAuthorization(AuthSetup.AdminOnly);
        review.MapGet("", async (string? status, int? page, int? pageSize, string? system, string? reason, string? q, HttpContext ctx,
            IIngestReviewRepository repository, ISystemRegistry systems) =>
        {
            var errors = Paging.Validate(page, pageSize);
            if (status is not null and not ("Pending" or "Resolved" or "Dismissed"))
                errors["status"] = ["Must be Pending, Resolved or Dismissed."];
            if (system is not null && systems.ById(system) is null)
                errors["system"] = ["Choose a registered system."];
            if (reason is not null and not ("NoDatMatch" or "AmbiguousGroup" or ReviewReasons.AmbiguousSet))
                errors["reason"] = ["Must be NoDatMatch, AmbiguousGroup or AmbiguousSet."];
            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            if (q?.Length > 100)
                errors["q"] = ["Must be at most 100 characters."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await repository.ListAsync(status ?? "Pending", page ?? 1, pageSize ?? Page<ReviewItem>.DefaultSize, ctx.RequestAborted,
                new ReviewFilter(system, reason, q));
            return Results.Ok(Paging.ToDto(result, item => new
            {
                id = item.Id, system = item.SystemId, reason = item.Reason, fileName = item.OriginalFileName,
                sizeBytes = item.SizeBytes, headerTitle = item.Header is null ? null : JsonNode.Parse(item.Header)?["title"]?.GetValue<string>(),
                createdAt = item.CreatedAt, isArchive = item.ArchivePath is not null,
            }));
        });
        review.MapGet("/{id:long}", async (long id, HttpContext ctx, IIngestReviewRepository repository, IGameRepository games,
            ISystemRegistry systems) =>
        {
            var item = await repository.GetAsync(id, ctx.RequestAborted);
            if (item is null)
                return Problems.NotFound(ctx);
            var candidates = new List<object>();
            foreach (var node in JsonNode.Parse(item.CandidateGameIds ?? "[]")!.AsArray())
                if (await games.GetAsync(node!.GetValue<Guid>(), ctx.RequestAborted) is { } game)
                    candidates.Add(new { id = game.Id, displayTitle = game.DisplayTitle });
            return Results.Ok(new
            {
                id = item.Id, system = item.SystemId, systemName = systems.ById(item.SystemId)?.DisplayName ?? item.SystemId,
                reason = item.Reason, sourcePath = item.SourcePath,
                archivePath = item.ArchivePath, archiveEntry = item.ArchiveEntry, originalFileName = item.OriginalFileName,
                sizeBytes = item.SizeBytes, crc32 = item.Crc32, md5 = item.Md5, sha1 = item.Sha1,
                header = Parse(item.Header), suggestions = Parse(item.Suggestions), datIdentification = Parse(item.DatIdentification),
                candidateGames = candidates, status = item.Status, gameId = item.GameId, createdAt = item.CreatedAt,
                lastSeenJobId = item.LastSeenJobId, resolvedAt = item.ResolvedAt, resolvedBy = item.ResolvedBy,
                resolvable = ReviewResolver.IsResolvable(item, systems),
            });
        });
        review.MapPost("/{id:long}/resolve", async (long id, ReviewResolutionRequest request, HttpContext ctx, ReviewResolver resolver) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            return ToResult(ctx, await resolver.ResolveAsync(id, request, AuditActor.From(ctx.User), ctx.RequestAborted));
        });
        review.MapPost("/{id:long}/dismiss", async (long id, HttpContext ctx, ReviewResolver resolver) =>
        {
            var result = await resolver.DismissAsync(id, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? Results.NoContent() : ToResult(ctx, result);
        });
        review.MapDelete("/{id:long}", async (long id, HttpContext ctx, ReviewResolver resolver) =>
        {
            var result = await resolver.DeleteSourceAsync(id, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? Results.NoContent() : ToResult(ctx, result);
        });
        api.MapGet("/dat/search", (string? system, string? q, int? limit, HttpContext ctx, ISystemRegistry systems, DatCache dats) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (system is null || systems.ById(system) is null)
                errors["system"] = ["Choose a registered system."];
            if (string.IsNullOrWhiteSpace(q))
                errors["q"] = ["A search term is required."];
            if (limit is < 1 or > 20)
                errors["limit"] = ["Must be 1–20."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            // B15: Redump repeats a game once per serial; each gameName is listed once (its first entry), before the limit.
            return Results.Ok((dats.For(system!)?.Roms ?? []).Where(r => r.GameName.Contains(q!, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(r => r.GameName, StringComparer.Ordinal).Take(limit ?? 20).Select(r => new { gameName = r.GameName, romName = r.RomName, sizeBytes = r.SizeBytes, crc32 = r.Crc32 }));
        }).RequireAuthorization(AuthSetup.AdminOnly);
        return api;
    }

    private static JsonNode? Parse(string? json) => json is null ? null : JsonNode.Parse(json);

    public static Dictionary<string, string[]> Validate(ReviewResolutionRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        switch (request.Action)
        {
            case "Dat":
                if (string.IsNullOrWhiteSpace(request.DatSystem)) errors["datSystem"] = ["A system is required."];
                if (string.IsNullOrWhiteSpace(request.DatGameName)) errors["datGameName"] = ["A DAT game is required."];
                break;
            case "Attach":
                if (request.TargetGameId is null || request.TargetGameId == Guid.Empty) errors["targetGameId"] = ["A target game is required."];
                if (request.Flag is not ("Hack" or "Translated" or "None")) errors["flag"] = ["Must be Hack, Translated or None."];
                break;
            case "Manual":
                if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 255 || FileNameSanitizer.Sanitize(request.Title).Length == 0)
                    errors["title"] = ["Must contain 1–255 characters and produce a nonempty filename."];
                ValidateList("regions", request.Regions, errors);
                ValidateList("languages", request.Languages, errors);
                ValidateList("flags", request.Flags, errors);
                if (request.Revision?.Length > 32) errors["revision"] = ["Must be at most 32 characters."];
                break;
            default: errors["action"] = ["Must be Dat, Attach or Manual."]; break;
        }
        if (request.Update == true && request.Action != "Dat")
            errors["update"] = [ReviewResolutionRequest.UpdateNeedsDat];
        return errors;
    }

    private static void ValidateList(string field, string[]? values, Dictionary<string, string[]> errors)
    {
        if (values is null || values.Any(string.IsNullOrWhiteSpace))
            errors[field] = ["An array of nonempty strings is required (it may be empty)."];
    }

    private static IResult ToResult(HttpContext ctx, ReviewResolutionResult result) => result.Code switch
    {
        null => Results.Ok(new { gameId = result.GameId }),
        "ValidationFailed" => Problems.Validation(ctx, result.Errors!),
        "NotFound" => Problems.NotFound(ctx),
        "JobRunning" or "SourceMissing" or "AmbiguousGroup" or "TargetZipUnreadable" or "InfoJsonNewerSchema" or "Duplicate"
            or ReviewResolver.SetFileNotResolvable or IngestErrors.NameTaken or IngestErrors.NameTooLong =>
            Problems.Create(ctx, 409, result.Code, result.Code),
        _ => Problems.Create(ctx, 500, result.Code, result.Code),
    };
}
