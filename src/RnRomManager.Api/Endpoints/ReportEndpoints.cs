using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Library;
using RnRomManager.Core.Reports;
using RnRomManager.Core.Systems;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Endpoints;

/// <summary><c>/api/reports</c>: the Reporting page (RP1–RP8). Any signed-in user can read, except Activity and Missing Metadata (Admin).</summary>
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

        // B4: the next game for the Next missing image shortcut, in the unfiltered report order.
        reports.MapGet("/missing-images/next", async (string? afterGameId, HttpContext ctx, IReportRepository repository) =>
        {
            Guid? after = null;
            if (afterGameId is not null)
            {
                if (!Guid.TryParse(afterGameId, out var parsed))
                    return Problems.Validation(ctx, "afterGameId", "Must be a game ID.");
                after = parsed;
            }
            var next = await repository.NextMissingImageAsync(after, ctx.RequestAborted);
            return Results.Ok(new { id = next.Id, currentIsOnly = next.CurrentIsOnly });
        });

        // Missing Metadata M2: Admin only (the group itself is any signed-in user).
        reports.MapGet("/missing-metadata", async (string? system, string? missing, string? q, string? sort, string? dir, int? page, int? pageSize,
            HttpContext ctx, IReportRepository repository, ISystemRegistry systems) =>
        {
            var errors = Validate(system, page, pageSize, systems);
            MissingMetadataField? field = missing?.ToLowerInvariant() switch
            {
                null or "any" => MissingMetadataField.Any,
                "year" => MissingMetadataField.Year,
                "developer" => MissingMetadataField.Developer,
                "publisher" => MissingMetadataField.Publisher,
                _ => null,
            };
            if (field is null)
                errors["missing"] = ["Must be any, year, developer or publisher."];
            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            if (q?.Length > 100)
                errors["q"] = ["Must be at most 100 characters."];
            MissingMetadataSort? sortKey = sort?.ToLowerInvariant() switch
            {
                null or "name" => MissingMetadataSort.Name,
                "system" => MissingMetadataSort.System,
                "year" => MissingMetadataSort.Year,
                _ => null,
            };
            if (sortKey is null)
                errors["sort"] = ["Must be name, system or year."];
            var descending = SortQuery.ParseDir(dir, errors, whenMissing: false);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var result = await repository.ListMissingMetadataAsync(system, field!.Value, q, sortKey!.Value, descending!.Value, page ?? 1,
                pageSize ?? 50, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, item => new
            {
                id = item.Id, displayTitle = item.DisplayTitle, system = item.SystemId, systemName = SystemName(systems, item.SystemId),
                releaseYear = item.ReleaseYear, developers = item.Developers, publishers = item.Publishers,
            }));
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // Missing IGDB IDs R1: validates exactly as /missing-metadata, with status in place of missing.
        reports.MapGet("/missing-igdb-ids", async (string? system, string? status, string? q, string? sort, string? dir, int? page, int? pageSize,
            HttpContext ctx, IReportRepository repository, ISystemRegistry systems) =>
        {
            var errors = Validate(system, page, pageSize, systems);
            MissingIgdbStatus? parsed = status?.ToLowerInvariant() switch
            {
                null or "missing" => MissingIgdbStatus.Missing,
                "not-on-igdb" => MissingIgdbStatus.NotOnIgdb,
                _ => null,
            };
            if (parsed is null)
                errors["status"] = ["Must be missing or not-on-igdb."];
            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            if (q?.Length > 100)
                errors["q"] = ["Must be at most 100 characters."];
            MissingMetadataSort? sortKey = sort?.ToLowerInvariant() switch
            {
                null or "name" => MissingMetadataSort.Name,
                "system" => MissingMetadataSort.System,
                "year" => MissingMetadataSort.Year,
                _ => null,
            };
            if (sortKey is null)
                errors["sort"] = ["Must be name, system or year."];
            var descending = SortQuery.ParseDir(dir, errors, whenMissing: false);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var result = await repository.ListMissingIgdbIdsAsync(system, parsed!.Value, q, sortKey!.Value, descending!.Value, page ?? 1,
                pageSize ?? 50, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, item => new
            {
                id = item.Id, displayTitle = item.DisplayTitle, system = item.SystemId, systemName = SystemName(systems, item.SystemId),
                releaseYear = item.ReleaseYear,
            }));
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // M3: the next game for the Next missing metadata shortcut, in the unfiltered default order.
        reports.MapGet("/missing-metadata/next", (string? afterGameId, HttpContext ctx, IReportRepository repository) =>
            NextAsync(afterGameId, ctx, repository.NextMissingMetadataAsync)).RequireAuthorization(AuthSetup.AdminOnly);

        // The next unlinked game for the Next missing IGDB ID shortcut, in the Missing IGDB IDs report's default order.
        reports.MapGet("/missing-igdb-ids/next", (string? afterGameId, HttpContext ctx, IReportRepository repository) =>
            NextAsync(afterGameId, ctx, repository.NextMissingIgdbIdAsync)).RequireAuthorization(AuthSetup.AdminOnly);

        // B21: the Admin-only Activity report (the group itself is any signed-in user). Audit lists cover the 30-day retention.
        reports.MapGet("/activity", async (HttpContext ctx, IReportRepository repository, IAuditRepository audit, IUserRepository users) =>
        {
            const int limit = 20;
            var token = ctx.RequestAborted;
            var lastSignIn = await users.GetLastSignInAsync(token);
            var added = await repository.ListRecentlyAddedRomsAsync(limit, token);
            async Task<IEnumerable<object>> Recent(params string[] actions) =>
                (await audit.ListRecentAsync(actions, limit, token)).Select(AuditEndpoints.ToDto);
            return Results.Ok(new
            {
                lastSignIn = lastSignIn is null ? null : new { username = lastSignIn.Username, at = DateTime.SpecifyKind(lastSignIn.At, DateTimeKind.Utc) },
                addedRoms = added.Select(r => new
                {
                    romId = r.RomId, gameId = r.GameId, gameTitle = r.GameTitle, fileName = r.FileName, system = r.System,
                    sizeBytes = r.SizeBytes, addedAt = DateTime.SpecifyKind(r.AddedAt, DateTimeKind.Utc),
                }),
                signIns = await Recent(AuditActions.SignedIn, AuditActions.SignInFailed, AuditActions.LockedOut),
                plays = await Recent(AuditActions.GamePlayed),
                saves = await Recent(AuditActions.SaveAdded, AuditActions.SaveUpdated, AuditActions.SaveDeleted),
                edits = await Recent(AuditActions.GameUpdated, AuditActions.ReleaseMoved, AuditActions.GamesMerged, AuditActions.CoverSet, AuditActions.PrimarySet, AuditActions.ReleaseEdited,
                    AuditActions.ImageAdded, AuditActions.ImageDeleted),
                deletions = await Recent(AuditActions.GameDeleted, AuditActions.ReleaseDeleted),
            });
        }).RequireAuthorization(AuthSetup.AdminOnly);

        reports.MapGet("/library-size", async (IReportRepository repository, ISystemRegistry systems, CancellationToken cancellationToken) =>
        {
            var sizes = await repository.ListSystemSizesAsync(cancellationToken);
            return Results.Ok(new
            {
                totalBytes = sizes.Sum(s => s.Bytes),
                gameCount = sizes.Sum(s => s.GameCount),
                updateCount = sizes.Sum(s => s.UpdateCount),
                systems = sizes.Select(s => new
                {
                    system = s.SystemId, systemName = SystemName(systems, s.SystemId), bytes = s.Bytes, gameCount = s.GameCount,
                    updateCount = s.UpdateCount,
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
                system, systemName = SystemName(systems, system), bytes = games.Sum(g => g.Bytes), updateCount = games.Sum(g => g.UpdateCount),
                games = games.Select(g => new
                {
                    id = g.Id, displayTitle = g.DisplayTitle, bytes = g.Bytes, romCount = g.RomCount, updateCount = g.UpdateCount,
                }),
            });
        });

        reports.MapGet("/duplicates", async (string? system, string? match, int? page, int? pageSize, HttpContext ctx, IReportRepository repository,
            ISystemRegistry systems) =>
        {
            var errors = Validate(system, page, pageSize, systems);
            DuplicateMatch? matchKind = match?.ToLowerInvariant() switch
            {
                null or "title" => DuplicateMatch.Title,
                "igdb" => DuplicateMatch.Igdb,
                _ => null,
            };
            if (matchKind is null)
                errors["match"] = ["Must be title or igdb."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await repository.ListDuplicatesAsync(matchKind!.Value, system, page ?? 1, pageSize ?? 25, ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, group => new
            {
                system = group.SystemId, systemName = SystemName(systems, group.SystemId), baseTitle = group.BaseTitle,
                gamesKey = group.GamesKey, igdbId = group.IgdbId,
                games = group.Games.Select(g => new
                {
                    id = g.Id, displayTitle = g.DisplayTitle, romCount = g.RomCount, zipSizeBytes = g.ZipSizeBytes, createdAt = g.CreatedAt,
                    boxArtUrl = g.BoxArtSha1 is { } sha1 ? LibraryEndpoints.CoverUrl(g.Id, sha1) : null,
                }),
            }));
        });

        // Multiple Releases B16: games with min or more releases, sorted and paged on the server.
        reports.MapGet("/multiple-releases", async (string? system, int? min, string? sort, string? dir, int? page, int? pageSize, HttpContext ctx,
            IReportRepository repository, ISystemRegistry systems) =>
        {
            var errors = Validate(system, page, pageSize, systems);
            if (min is < 2 or > 10000)
                errors["min"] = ["Must be 2–10000."];
            MultipleReleaseSort? sortKey = sort?.ToLowerInvariant() switch
            {
                null or "releases" => MultipleReleaseSort.Releases,
                "name" => MultipleReleaseSort.Name,
                "system" => MultipleReleaseSort.System,
                "updates" => MultipleReleaseSort.Updates,
                "size" => MultipleReleaseSort.Size,
                _ => null,
            };
            if (sortKey is null)
                errors["sort"] = ["Must be name, system, releases, updates or size."];
            var descending = SortQuery.ParseDir(dir, errors, whenMissing: null);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            descending ??= sortKey is not (MultipleReleaseSort.Name or MultipleReleaseSort.System);
            var result = await repository.ListMultipleReleasesAsync(system, min ?? 2, sortKey!.Value, descending.Value, page ?? 1, pageSize ?? 50,
                ctx.RequestAborted);
            var maxReleases = await repository.MaxReleaseCountAsync(system, ctx.RequestAborted);
            return Results.Ok(new
            {
                maxReleases,
                items = result.Items.Select(item => new
                {
                    id = item.Id, displayTitle = item.DisplayTitle, system = item.SystemId,
                    systemName = SystemName(systems, item.SystemId), releaseCount = item.ReleaseCount,
                    updateCount = item.UpdateCount, bytes = item.Bytes,
                }),
                page = result.PageNumber,
                pageSize = result.PageSize,
                total = result.Total,
            });
        });

        // Release editing B14: the 10 newest games per system, systems by name.
        reports.MapGet("/newly-added", async (string? system, HttpContext ctx, IReportRepository repository, ISystemRegistry systems) =>
        {
            if (Validate(system, null, null, systems) is { Count: > 0 } errors)
                return Problems.Validation(ctx, errors);
            var games = await repository.ListNewlyAddedGamesAsync(system, 10, ctx.RequestAborted);
            return Results.Ok(new
            {
                systems = games.GroupBy(g => g.SystemId)
                    .Select(group => (Id: group.Key, Name: SystemName(systems, group.Key), Games: group))
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new
                    {
                        system = s.Id,
                        systemName = s.Name,
                        games = s.Games.Select(g => new { id = g.Id, displayTitle = g.DisplayTitle, addedAt = g.AddedAt, releaseCount = g.ReleaseCount }),
                    }),
            });
        });

        // Release editing B13: Games With Hacks.
        reports.MapGet("/hacks", async (string? system, string? sort, string? dir, int? page, int? pageSize, HttpContext ctx,
            IReportRepository repository, ISystemRegistry systems) =>
        {
            var errors = Validate(system, page, pageSize, systems);
            HackedGameSort? sortKey = sort?.ToLowerInvariant() switch
            {
                null or "hacks" => HackedGameSort.Hacks,
                "name" => HackedGameSort.Name,
                "system" => HackedGameSort.System,
                "releases" => HackedGameSort.Releases,
                "size" => HackedGameSort.Size,
                _ => null,
            };
            if (sortKey is null)
                errors["sort"] = ["Must be name, system, hacks, releases or size."];
            var descending = SortQuery.ParseDir(dir, errors, whenMissing: null);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            descending ??= sortKey is not (HackedGameSort.Name or HackedGameSort.System);
            var result = await repository.ListHackedGamesAsync(system, sortKey!.Value, descending.Value, page ?? 1, pageSize ?? 50,
                ctx.RequestAborted);
            return Results.Ok(new
            {
                items = result.Items.Select(item => new
                {
                    id = item.Id, displayTitle = item.DisplayTitle, system = item.SystemId,
                    systemName = SystemName(systems, item.SystemId), hackCount = item.HackCount,
                    releaseCount = item.ReleaseCount, bytes = item.Bytes,
                }),
                page = result.PageNumber,
                pageSize = result.PageSize,
                total = result.Total,
            });
        });

        // Image Resolutions R1–R4: non-placeholder images, smallest first.
        reports.MapGet("/image-resolutions", async (string? system, string? type, int? maxWidth, int? maxHeight, int? page, int? pageSize,
            HttpContext ctx, IReportRepository repository, ISystemRegistry systems) =>
        {
            var errors = Validate(system, page, pageSize, systems);
            ImageType? imageType = null;
            if (type is not null)
            {
                // Enum.TryParse accepts numbers, so all-digit values are rejected first (R1).
                if (!type.All(char.IsAsciiDigit) && Enum.TryParse<ImageType>(type, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                    imageType = parsed;
                else
                    errors["type"] = ["Must be BoxArt, Snap, TitleScreen or Other."];
            }
            if (maxWidth is < 1 or > 100000)
                errors["maxWidth"] = ["Must be 1–100000."];
            if (maxHeight is < 1 or > 100000)
                errors["maxHeight"] = ["Must be 1–100000."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var result = await repository.ListImageResolutionsAsync(system, imageType, maxWidth, maxHeight, page ?? 1, pageSize ?? 50,
                ctx.RequestAborted);
            return Results.Ok(Paging.ToDto(result, item => new
            {
                imageId = item.ImageId, gameId = item.GameId, displayTitle = item.DisplayTitle, system = item.SystemId,
                systemName = SystemName(systems, item.SystemId), type = item.Type.ToString(), width = item.Width, height = item.Height,
                sizeBytes = item.SizeBytes,
            }));
        });

        reports.MapPost("/duplicates/dismiss",async (DismissDuplicateRequest request, HttpContext ctx, IReportRepository repository,
            ISystemRegistry systems, TimeProvider time) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrEmpty(request.System) || systems.ById(request.System) is null)
                errors["system"] = ["Choose a registered system."];
            // D3: a title dismissal gives baseTitle, an IGDB one igdbId ("given" = present and not JSON null).
            var igdbGiven = request.IgdbId is { ValueKind: not JsonValueKind.Null };
            int igdbId = 0;
            if (igdbGiven == (request.BaseTitle is not null))
                errors["igdbId"] = ["Give baseTitle or igdbId, not both."];
            else if (igdbGiven && !(request.IgdbId!.Value.ValueKind == JsonValueKind.Number && request.IgdbId.Value.TryGetInt32(out igdbId) && igdbId >= 1))
                errors["igdbId"] = ["Must be a positive whole number."];
            else if (!igdbGiven && (request.BaseTitle!.Length is 0 or > 255))
                errors["baseTitle"] = ["Must contain 1–255 characters."];
            if (request.GamesKey is null || !GamesKeyPattern().IsMatch(request.GamesKey))
                errors["gamesKey"] = ["Must be 64 lower-case hex characters."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            // A key that matches no current group is stored anyway: the group may have just changed (RP8).
            await repository.DismissDuplicateAsync(igdbGiven ? DuplicateMatch.Igdb : DuplicateMatch.Title, request.System!,
                igdbGiven ? igdbId.ToString(CultureInfo.InvariantCulture) : request.BaseTitle!, request.GamesKey!, EndpointUser.Id(ctx.User),
                time.GetUtcNow().UtcDateTime, ctx.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization(AuthSetup.AdminOnly);

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
            var actor = AuditActor.From(ctx.User);
            foreach (var source in sources)
            {
                var result = await editor.MergeAsync(source, request.TargetGameId!.Value, actor, ctx.RequestAborted);
                if (result.Code is null)
                {
                    merged.Add(source);
                    continue;
                }
                // DbWriteFailed: the target ZIP was written and the source ZIP deleted, so its files did move,
                // unless the target was lost and the source kept (W4).
                if (result.Code == LibraryEditResult.DbWriteFailed && !result.TargetPublicationLost)
                    merged.Add(source);
                return LibraryEndpoints.ToProblem(ctx, result, new Dictionary<string, object?> { ["merged"] = merged });
            }
            return Results.Ok(new { targetGameId = request.TargetGameId });
        }).RequireAuthorization(AuthSetup.AdminOnly);

        return api;
    }

    private const int MaxMergeSources = 20;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex GamesKeyPattern();

    /// <summary>The paged reports' shared rules: house paging, and an optional registered <c>system</c>.</summary>
    private static Dictionary<string, string[]> Validate(
        string? system,
        int? page,
        int? pageSize,
        ISystemRegistry systems)
    {
        var errors = Paging.Validate(page, pageSize);
        if (system is not null && systems.ById(system) is null)
            errors["system"] = ["Choose a registered system."];
        return errors;
    }

    private static string SystemName(ISystemRegistry systems, string id) => systems.ById(id)?.DisplayName ?? id;

    /// <summary>A "next game" shortcut's answer: <c>afterGameId</c> must be a GUID when given (400 otherwise).</summary>
    private static async Task<IResult> NextAsync(string? afterGameId, HttpContext ctx,
        Func<Guid?, CancellationToken, Task<MissingImageSuccessor>> next)
    {
        Guid? after = null;
        if (afterGameId is not null)
        {
            if (!Guid.TryParse(afterGameId, out var parsed))
                return Problems.Validation(ctx, "afterGameId", "Must be a game ID.");
            after = parsed;
        }
        var found = await next(after, ctx.RequestAborted);
        return Results.Ok(new { id = found.Id, currentIsOnly = found.CurrentIsOnly });
    }
}
