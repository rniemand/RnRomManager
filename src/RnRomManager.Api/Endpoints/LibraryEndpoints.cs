using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using RnRomManager.Api.Auth;
using RnRomManager.Api.Http;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Database;
using RnRomManager.Core.Dats;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Integrity;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Systems;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// Library reads for every signed-in user and Admin edits (spec §23 <c>/api/systems</c>, <c>/api/games*</c>): lists and details come from
/// the DB mirror, image and ROM bytes are streamed from the game's ZIP.
/// </summary>
public static class LibraryEndpoints
{
    /// <summary>
    /// Play B1: the browser cores that play a release stored outside the ZIP: through play-disc (<see cref="PlayDiscCores"/>) PCSX ReARMed for
    /// PlayStation, (PSP B1) PPSSPP for PlayStation Portable, (Saturn B6) Yabause for Saturn and (Sega CD B5) Genesis Plus GX
    /// for Sega CD, (TurboGrafx-CD B5) Beetle PCE for TurboGrafx-CD, (PC-FX X6) Beetle PC-FX for PC-FX. Eligibility is per core, so Genesis Plus GX also accepts an external primary of Mega Drive, a family that
    /// the registry keeps embedded. S8: DeSmuME plays an external DS release from <c>/download</c>, and (MS-DOS P3) DOSBox Pure an
    /// external DOS game ZIP. 3DS P5: azahar plays an external <c>.3ds</c>/<c>.cci</c> from <c>/download</c>. Arcade P5: MAME 2003-Plus an
    /// external set ZIP (<c>.mamez</c>), with its parent and BIOS sets from <c>/romset</c>; FBNeo P3 FinalBurn Neo a <c>.fbneoz</c>/<c>.fbneo7z</c> likewise.
    /// </summary>
    public static readonly IReadOnlySet<string> ExternalBrowserCores = new HashSet<string>(StringComparer.Ordinal) { "pcsx_rearmed", "ppsspp", "yabause", "genesis_plus_gx", "opera", "desmume", "mednafen_pce", "dosbox_pure", "azahar", "mame2003_plus", "fbneo", "mednafen_pcfx" };

    /// <summary>S8: the cores whose external releases play through <c>play-disc</c>; any other (DS) loads from <c>/download</c>.</summary>
    public static readonly IReadOnlySet<string> PlayDiscCores = new HashSet<string>(StringComparer.Ordinal) { "pcsx_rearmed", "ppsspp", "yabause", "genesis_plus_gx", "opera", "mednafen_pce", "mednafen_pcfx" };

    /// <summary>B10: <c>&lt;title&gt;-&lt;imageId&gt;.&lt;ext&gt;</c>; the suffix is added after sanitizing (which cuts to 180), so it is never lost.</summary>
    public static string ImageDownloadName(string displayTitle, ImageRow image)
    {
        var title = FileNameSanitizer.Sanitize(displayTitle);
        return $"{(title.Length == 0 ? "image" : title)}-{image.Id}{Path.GetExtension(image.FileName)}";
    }

    public static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder api)
    {
        // Release editing B11: the Edit release form's region list.
        api.MapGet("/systems/regions", () => Results.Ok(NoIntroRegions.FormOrder));

        api.MapGet("/systems", async (HttpContext ctx, ISystemRegistry systems, IGameRepository games, IDiscoveryLogoRepository logos) =>
        {
            var stats = (await games.ListSystemStatsAsync(ctx.RequestAborted)).ToDictionary(s => s.SystemId);
            // B15: the shared logo's URL, keyed by the system id; stored under its digest.
            var updated = await logos.ListUpdatedAtAsync(LogoKind.Systems, systems.All.Select(h => DiscoveryKey.ForSystem(h.Id)).ToList(), ctx.RequestAborted);
            return Results.Ok(systems.All.OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase).Select(handler =>
            {
                var logoUrl = updated.TryGetValue(DiscoveryKey.ForSystem(handler.Id), out var at) ? DiscoveryLogoEndpoints.LogoUrl(LogoKind.Systems, handler.Id, at) : null;
                return stats.TryGetValue(handler.Id, out var s)
                    ? new { id = handler.Id, displayName = handler.DisplayName, shortName = handler.ShortName, embedRoms = handler.EmbedRomsInZip, gameCount = s.GameCount, romCount = s.RomCount, regions = s.Regions, logoUrl }
                    : new { id = handler.Id, displayName = handler.DisplayName, shortName = handler.ShortName, embedRoms = handler.EmbedRomsInZip, gameCount = 0, romCount = 0, regions = (IReadOnlyList<string>)[], logoUrl };
            }));
        });

        var games = api.MapGroup("/games");

        games.MapGet("", async (string? system, string? letter, string? region, string? q, string? sort, string? dir, int? page,
            int? pageSize, string? favourite, string? collection, string? notInCollection, HttpContext ctx, ISystemRegistry systems, IGameRepository repository,
            ICollectionRepository collections) =>
        {
            var (query, errors) = ParseQuery(system, letter, region, q, sort, dir, page, pageSize, systems, favourite, collection, notInCollection);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            // B15/P1: another user's collection answers 404 like a missing one, before any membership is queried.
            foreach (var collectionId in new[] { query.CollectionId, query.NotInCollectionId })
                if (collectionId is { } id && await collections.GetAsync(id, CollectionEndpoints.UserId(ctx), ctx.RequestAborted) is null)
                    return Problems.NotFound(ctx);
            var result = await ForViewerAsync(await repository.ListAsync(query, ctx.RequestAborted), ctx, repository);
            return Results.Ok(Paging.ToDto(result, game => ToListItemDto(game, systems)));
        });

        // F1: per-option counts for the Library filters; each facet ignores its own filter. Sort, paging and collections aren't read.
        games.MapGet("/facets", async (string? system, string? letter, string? region, string? q, string? favourite, HttpContext ctx,
            ISystemRegistry systems, IGameRepository repository) =>
        {
            var (query, errors) = ParseQuery(system, letter, region, q, null, null, null, null, systems, favourite);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var facets = await repository.ListFacetsAsync(query, ctx.RequestAborted);
            return Results.Ok(new
            {
                systems = facets.Systems.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { id = p.Key, count = p.Value }),
                regions = facets.Regions.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { region = p.Key, count = p.Value }),
                letters = facets.Letters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { letter = p.Key, count = p.Value }),
            });
        });

        games.MapGet("/{id:guid}", (Guid id, HttpContext ctx, IGameRepository repository, IRomRepository roms, IImageRepository images) =>
            DetailAsync(id, ctx, repository, roms, images));

        // B10: ?download=true sends it as an attachment named after the game.
        games.MapGet("/{id:guid}/images/{imageId:long}", async (Guid id, long imageId, bool? download, HttpContext ctx, IGameRepository repository,
            IImageRepository images, LibraryFiles files) =>
        {
            if (await images.GetAsync(id, imageId, ctx.RequestAborted) is not { } image
                || await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);

            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            // A 304 for a matching If-None-Match is answered by the stream result itself.
            return files.OpenEntry(ctx, game, LibraryZip.ImageEntry(image.FileName), ImageFormats.MaxBytes, out var stream) is { } problem
                ? problem
                : Results.Stream(stream!, ImageFormats.ContentType(image.FileName), download == true ? ImageDownloadName(game.DisplayTitle, image) : null,
                    entityTag: new EntityTagHeaderValue($"\"{image.Sha1}\""));
        });

        // C3: the cached WebP of the current cover; a stale sha1 is a 404, so only the database's value builds a path.
        games.MapGet("/{id:guid}/cover/{sha1}", async (Guid id, string sha1, HttpContext ctx, IGameRepository repository,
            IImageRepository images, LibraryFiles files, CoverCache cache, ILogger<CoverCache> logger) =>
        {
            var cover = (await images.ListByGameAsync(id, ctx.RequestAborted)).Where(i => i.IsCover).OrderBy(i => i.Id).FirstOrDefault();
            if (cover is null || !string.Equals(cover.Sha1, sha1, StringComparison.Ordinal)
                || await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);

            var etag = new EntityTagHeaderValue($"\"{cover.Sha1}\"");
            if (cache.CachedPath(id, cover.Sha1) is { } path)
            {
                ctx.Response.Headers.CacheControl = CoverCacheControl;
                return Results.File(path, "image/webp", entityTag: etag);
            }

            var entry = LibraryZip.ImageEntry(cover.FileName);
            if (files.OpenEntry(ctx, game, entry, ImageFormats.MaxBytes, out var stream) is { } problem)
                return problem;
            CoverCacheResult result;
            await using (stream)
                result = await cache.GenerateAsync(id, cover.Sha1, stream!, ctx.RequestAborted);

            if (result.Webp is { } webp)
            {
                ctx.Response.Headers.CacheControl = CoverCacheControl;
                return Results.Bytes(webp, "image/webp", entityTag: etag);
            }

            logger.LogWarning("The cover of game {GameId} couldn't be converted to WebP; serving the original.", id);
            if (files.OpenEntry(ctx, game, entry, ImageFormats.MaxBytes, out var original) is { } reopenProblem)
                return reopenProblem;
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.Stream(original!, ImageFormats.ContentType(cover.FileName), entityTag: etag);
        });

        games.MapGet("/{id:guid}/download", async (Guid id, HttpContext ctx, IGameRepository repository, LibraryFiles files) =>
        {
            if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            if (files.OpenZip(ctx, game, out var stream) is { } problem)
                return problem;
            var name = game.ZipPath[(game.ZipPath.LastIndexOf('/') + 1)..];
            // fileDownloadName sets Content-Disposition with both filename and RFC 5987 filename*.
            return Results.Stream(stream!, "application/zip", name, File.GetLastWriteTimeUtc(stream!.SafeFileHandle), enableRangeProcessing: true);
        }).WithMetadata(MaintenanceExempt.Instance);

        // Z2: the ZIP's file entries for the Zip Contents tab.
        games.MapGet("/{id:guid}/zip-entries", async (Guid id, HttpContext ctx, IGameRepository repository, LibraryFiles files) =>
        {
            if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            if (files.ListEntries(ctx, game, out var entries) is { } problem)
                return problem;
            return Results.Ok(new
            {
                zipName = game.ZipPath[(game.ZipPath.LastIndexOf('/') + 1)..],
                entries = entries!.Select(e => new
                {
                    path = e.Path, sizeBytes = e.SizeBytes, compressedSizeBytes = e.CompressedSizeBytes, modifiedUtc = e.ModifiedUtc,
                }),
            });
        });

        // Z3: one file entry as a download; not audited (downloads never are).
        games.MapGet("/{id:guid}/zip-entries/download", async (Guid id, string? path, HttpContext ctx, IGameRepository repository,
            LibraryFiles files) =>
        {
            if (string.IsNullOrEmpty(path))
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["path"] = ["Required."] });
            if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            return files.OpenRawEntry(ctx, game, path, out var stream) is { } problem
                ? problem
                : Results.Stream(stream!, "application/octet-stream", path[(path.LastIndexOf('/') + 1)..]);
        }).WithMetadata(MaintenanceExempt.Instance);

        // B16: the caller's collections holding this game.
        games.MapGet("/{id:guid}/collections", async (Guid id, HttpContext ctx, IGameRepository repository, ICollectionRepository collections) =>
        {
            if (await repository.GetAsync(id, ctx.RequestAborted) is null)
                return Problems.NotFound(ctx);
            var list = await collections.ListForGameAsync(id, CollectionEndpoints.UserId(ctx), ctx.RequestAborted);
            return Results.Ok(list.Select(c => new { id = c.Id, name = c.Name }));
        });

        games.MapGet("/{id:guid}/info-json", async (Guid id, HttpContext ctx, IGameRepository repository, LibraryFiles files) =>
        {
            if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            // Returned as stored, even when it isn't valid JSON (GP4 shows the raw text then).
            return files.ReadEntry(ctx, game, LibraryZip.InfoJsonEntry, InfoJsonReader.MaxBytes, out var bytes) is { } problem
                ? problem
                : Results.Bytes(bytes!, "application/json");
        });

        // The open/check logic is shared with the bundle ticket stream (LibraryDownload).
        games.MapGet("/{id:guid}/roms/{romId:long}/download", async (Guid id, long romId, HttpContext ctx, IGameRepository repository,
            IRomRepository roms, LibraryFiles files) =>
        {
            var (attachment, problem) = await LibraryDownload.OpenReleaseAsync(ctx, id, romId, repository, roms, files);
            return problem ?? LibraryDownload.Send(ctx, attachment!);
        }).WithMetadata(MaintenanceExempt.Instance);

        // Arcade P2: the parent and BIOS romsets a MAME or FBNeo release needs beside it, from the library (FBNeo BIOS: by name).
        games.MapGet("/{id:guid}/roms/{romId:long}/romset", async (Guid id, long romId, HttpContext ctx, IGameRepository repository,
            IRomRepository roms, ISystemRegistry systems, DatCache dats) =>
        {
            if (await roms.GetAsync(id, romId, ctx.RequestAborted) is not { } rom
                || await repository.GetAsync(id, ctx.RequestAborted) is null
                || systems.ById(rom.SystemId)?.DatSource.IsRomset() != true)
                return Problems.NotFound(ctx);
            return Results.Ok(await MameRomset.ResolveAsync(rom, systems.ById(rom.SystemId)!.DatSource, dats.For(rom.SystemId), roms, ctx.RequestAborted));
        }).WithMetadata(MaintenanceExempt.Instance);

        // B20: one file of a set, found by the release it belongs to (another release's file id is a 404).
        games.MapGet("/{id:guid}/roms/{romId:long}/files/{fileId:long}/download", async (Guid id, long romId, long fileId, HttpContext ctx,
            IGameRepository repository, IRomRepository roms, LibraryFiles files) =>
        {
            var (attachment, problem) = await LibraryDownload.OpenSetFileAsync(ctx, id, romId, fileId, repository, roms, files);
            return problem ?? LibraryDownload.Send(ctx, attachment!);
        }).WithMetadata(MaintenanceExempt.Instance);

        // Play B2–B3: one PlayStation disc for the browser emulator (a standalone image, or its cue set as a ZIP).
        games.MapGet("/{id:guid}/roms/{romId:long}/play-disc", PlayDisc.HandleAsync).WithMetadata(MaintenanceExempt.Instance);
        games.MapGet("/{id:guid}/roms/{romId:long}/play-disc-set", PlayDisc.HandleSetAsync).WithMetadata(MaintenanceExempt.Instance);

        // B4–B7 release notes: any signed-in user reads them; writes are admin-only (U3), not audited.
        games.MapGet("/{id:guid}/roms/{romId:long}/note", async (Guid id, long romId, HttpContext ctx, IGameRepository repository,
            IRomRepository roms, LibraryFiles files) =>
        {
            if (await roms.GetAsync(id, romId, ctx.RequestAborted) is not { } rom
                || await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            return files.ReadNote(ctx, game, rom.FileName, out var bytes) is { } problem
                ? problem
                : Results.Bytes(bytes!, "text/markdown; charset=utf-8");
        });

        // B22: a browser play started (modal or PSP tab); audited for the Activity report, nothing else.
        games.MapPost("/{id:guid}/roms/{romId:long}/played", async (Guid id, long romId, HttpContext ctx, IGameRepository repository,
            IRomRepository roms, IAuditLog audit) =>
        {
            if (await roms.GetAsync(id, romId, ctx.RequestAborted) is not { } rom
                || await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            audit.Record(new AuditEvent(AuditActions.GamePlayed, AuditActor.From(ctx.User), game.SystemId, game.Id,
                AuditPaths.Library(game.ZipPath), Details: rom.FileName));
            return Results.NoContent();
        });

        games.MapPut("/{id:guid}/roms/{romId:long}/note",async (Guid id, long romId, JsonElement body, HttpContext ctx, LibraryEditor editor) =>
        {
            var (text, errors) = ParseNote(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            return ToResult(ctx, await editor.SaveNoteAsync(id, romId, text!, AuditActor.From(ctx.User).UserId, ctx.RequestAborted));
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapDelete("/{id:guid}/roms/{romId:long}/note", async (Guid id, long romId, HttpContext ctx, LibraryEditor editor) =>
            ToResult(ctx, await editor.DeleteNoteAsync(id, romId, AuditActor.From(ctx.User).UserId, ctx.RequestAborted)))
            .RequireAuthorization(AuthSetup.AdminOnly);

        // Admin edits (§11, D28, D35): each takes the job lock inside LibraryEditor.
        games.MapPatch("/{id:guid}", async (Guid id, JsonElement body, HttpContext ctx, LibraryEditor editor, IGameRepository repository,
            IRomRepository roms, IImageRepository images) =>
        {
            var (patch, errors) = ParsePatch(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.PatchAsync(id, patch, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // Admin-only (U3; shared per game); takes the job lock and rewrites the ZIP like every edit.
        games.MapPatch("/{id:guid}/rating", async (Guid id, JsonElement body, HttpContext ctx, LibraryEditor editor, IGameRepository repository,
            IRomRepository roms, IImageRepository images) =>
        {
            var (patch, errors) = ParseRating(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.PatchAsync(id, patch, null, ctx.RequestAborted); // rating/favourite: not audited
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, LibraryEditor editor) =>
            ToResult(ctx, await editor.DeleteGameAsync(id, AuditActor.From(ctx.User), ctx.RequestAborted))).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapDelete("/{id:guid}/roms/{romId:long}", async (Guid id, long romId, HttpContext ctx, LibraryEditor editor) =>
            ToResult(ctx, await editor.DeleteReleaseAsync(id, romId, AuditActor.From(ctx.User), ctx.RequestAborted))).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapPost("/{id:guid}/roms/{romId:long}/primary", async (Guid id, long romId, HttpContext ctx, LibraryEditor editor,
            IGameRepository repository, IRomRepository roms, IImageRepository images) =>
        {
            var result = await editor.SetPrimaryReleaseAsync(id, romId, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // Release editing B4.
        games.MapPatch("/{id:guid}/roms/{romId:long}", async (Guid id, long romId, JsonElement body, HttpContext ctx, LibraryEditor editor) =>
        {
            var (patch, errors) = ParseReleasePatch(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.EditReleaseAsync(id, romId, patch, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? Results.Ok(new { fileName = result.FileName }) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapPost("/{id:guid}/roms/{romId:long}/move", async (Guid id, long romId, MoveReleaseRequest request, HttpContext ctx,
            LibraryEditor editor) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.MoveReleaseAsync(id, romId, request.TargetGameId, request.NewGameTitle?.Trim(), AuditActor.From(ctx.User),
                ctx.RequestAborted);
            return result.Code is null ? Results.Ok(new { targetGameId = result.GameId }) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapPost("/{id:guid}/media/retry", async (Guid id, HttpContext ctx, LibraryEditor editor, IGameRepository repository,
            IRomRepository roms, IImageRepository images) =>
        {
            var result = await editor.RetryMediaAsync(id, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // I5–I7: image management. The upload reads the form itself (no IFormFile binding), so the group's own
        // X-XSRF-TOKEN filter is the antiforgery check, as for every other mutation.
        games.MapPost("/{id:guid}/images", async (Guid id, HttpContext ctx, LibraryEditor editor, IGameRepository repository,
            IRomRepository roms, IImageRepository images, JobRunner runner, IntegrityJobFactory integrity, ILoggerFactory loggers,
            ISystemRegistry systems, JobLock jobLock, IIntegrityIssueRepository issues, TimeProvider time) =>
        {
            var (form, problem) = await ReadImageFormAsync(ctx);
            if (problem is not null)
                return problem;
            var errors = new Dictionary<string, string[]>();
            // Exact names only: Enum.TryParse would also accept "0" or "boxart".
            var type = form!["type"].ToString() switch { "BoxArt" => ImageType.BoxArt, "Snap" => ImageType.Snap, _ => (ImageType?)null };
            if (type is null)
                errors["type"] = ["Choose BoxArt or Snap."];
            var bytes = await ReadImageFileAsync(form, errors, ctx.RequestAborted);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.AddImageAsync(id, type!.Value, bytes!, AuditActor.From(ctx.User), ctx.RequestAborted);
            await StartGameCheckAsync(result, id, ctx, repository, runner, integrity, loggers, systems, jobLock, issues, time);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(UploadRequestLimit));

        // U1–U5: the server downloads the image (public addresses only), then stores it exactly like an upload.
        games.MapPost("/{id:guid}/images/from-url", async (Guid id, ImageUrlRequest request, HttpContext ctx, ImageUrlDownloader downloader,
            LibraryEditor editor, IGameRepository repository, IRomRepository roms, IImageRepository images, JobRunner runner,
            IntegrityJobFactory integrity, ILoggerFactory loggers, ISystemRegistry systems, JobLock jobLock, IIntegrityIssueRepository issues,
            TimeProvider time) =>
        {
            var errors = Validate(request, out var url, out var type);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            if (await repository.GetAsync(id, ctx.RequestAborted) is null)
                return Problems.NotFound(ctx);
            var download = await downloader.DownloadAsync(url!, ctx.RequestAborted);
            if (download.Bytes is not { } bytes)
                return Problems.Create(ctx, download.Status, download.Code!, download.Title!);
            if (DownloadedImageError(bytes) is { } bytesError)
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["url"] = [bytesError] });
            var result = await editor.AddImageAsync(id, type, bytes, AuditActor.From(ctx.User), ctx.RequestAborted, request.Url);
            await StartGameCheckAsync(result, id, ctx, repository, runner, integrity, loggers, systems, jobLock, issues, time);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // IGDB suggestions S8: read-only candidates for the Suggest Images dialog; adding one is the from-url route above.
        games.MapGet("/{id:guid}/image-suggestions", async (Guid id, string? type, HttpContext ctx, IGameRepository repository,
            IImageSuggestions suggestions) =>
        {
            ImageType? parsed = type switch { "BoxArt" => ImageType.BoxArt, "Snap" => ImageType.Snap, _ => null };
            if (parsed is null)
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["type"] = ["Choose BoxArt or Snap."] });
            if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            if (!suggestions.Configured)
                return Problems.Create(ctx, StatusCodes.Status409Conflict, IgdbNotConfigured,
                    "Image suggestions need Sources:IgdbClientId and Sources:IgdbClientSecret.");
            var result = await suggestions.SuggestAsync(game.DisplayTitle, game.SystemId, parsed.Value, ctx.RequestAborted);
            return result.Suggestions is { } list
                ? Results.Ok(new { suggestions = list })
                : Problems.Create(ctx, StatusCodes.Status502BadGateway, IgdbFailed, result.FailedTitle!);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // L1/L2: read-only, so no job lock and no audit row.
        games.MapGet("/{id:guid}/igdb-matches", async (Guid id, HttpContext ctx, IGameRepository repository, IGameInfoLookup lookup) =>
        {
            string? q = null;
            if (ctx.Request.Query.TryGetValue("q", out var values))
            {
                q = values.ToString().Trim();
                if (q.Length is 0 or > MaxLookupQuery || IgdbClient.SearchTitle(q).Length == 0)
                    return Problems.Validation(ctx, new Dictionary<string, string[]> { ["q"] = ["Must be 1–100 characters."] });
            }
            if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            if (!lookup.Configured)
                return Problems.Create(ctx, StatusCodes.Status409Conflict, IgdbNotConfigured,
                    "Metadata lookup needs Sources:IgdbClientId and Sources:IgdbClientSecret.");
            q ??= game.DisplayTitle.Length > MaxLookupQuery ? game.DisplayTitle[..MaxLookupQuery] : game.DisplayTitle;
            var result = await lookup.LookupAsync(q, game.SystemId, ctx.RequestAborted);
            return result.Matches is { } matches
                ? Results.Ok(new { matches })
                : Problems.Create(ctx, StatusCodes.Status502BadGateway, IgdbFailed, result.FailedTitle!);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapDelete("/{id:guid}/images/{imageId:long}", async (Guid id, long imageId, HttpContext ctx, LibraryEditor editor,
            IGameRepository repository, IRomRepository roms, IImageRepository images) =>
        {
            var result = await editor.DeleteImageAsync(id, imageId, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapPost("/{id:guid}/images/{imageId:long}/cover", async (Guid id, long imageId, HttpContext ctx, LibraryEditor editor,
            IGameRepository repository, IRomRepository roms, IImageRepository images) =>
        {
            var result = await editor.SetCoverAsync(id, imageId, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        api.MapPost("/jobs/media-retry", async (HttpContext ctx, JobRunner runner, MediaRetryJob job) =>
        {
            var started = await runner.StartAsync(job, JobTrigger.Manual, DatEndpoints.UserId(ctx.User), ctx.RequestAborted);
            return started is null ? DatEndpoints.JobRunning(ctx) : Results.Accepted(value: new { jobId = started.Id });
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // Admin actions R1.
        api.MapPost("/jobs/igdb-sync", async (HttpContext ctx, JobRunner runner, IgdbSyncJob job, IIgdbGames igdb) =>
        {
            if (!igdb.Configured)
                return Problems.Create(ctx, StatusCodes.Status409Conflict, IgdbNotConfigured,
                    "Refreshing ratings needs Sources:IgdbClientId and Sources:IgdbClientSecret.");
            var started = await runner.StartAsync(job, JobTrigger.Manual, DatEndpoints.UserId(ctx.User), ctx.RequestAborted);
            return started is null ? DatEndpoints.JobRunning(ctx) : Results.Accepted(value: new { jobId = started.Id });
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // Admin actions P1.
        api.MapPost("/jobs/pull-covers", async (HttpContext ctx, JobRunner runner, PullCoversJob job, IIgdbGames igdb) =>
        {
            if (!igdb.Configured)
                return Problems.Create(ctx, StatusCodes.Status409Conflict, IgdbNotConfigured,
                    "Pulling covers needs Sources:IgdbClientId and Sources:IgdbClientSecret.");
            var started = await runner.StartAsync(job, JobTrigger.Manual, DatEndpoints.UserId(ctx.User), ctx.RequestAborted);
            return started is null ? DatEndpoints.JobRunning(ctx) : Results.Accepted(value: new { jobId = started.Id });
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapPost("/{id:guid}/merge", async (Guid id, MergeRequest request, HttpContext ctx, LibraryEditor editor) =>
        {
            if (request.TargetGameId is not { } target || target == Guid.Empty)
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["targetGameId"] = ["A target game is required."] });
            var result = await editor.MergeAsync(id, target, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? Results.Ok(new { targetGameId = result.GameId }) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        return api;
    }

    /// <summary>IGDB suggestions S8: the ProblemDetails codes.</summary>
    public const string IgdbNotConfigured = "IgdbNotConfigured";

    public const string IgdbFailed = "IgdbFailed";

    /// <summary>L1: the longest <c>q</c> (and the cut of the default display title).</summary>
    private const int MaxLookupQuery = 100;

    /// <summary>The upload's whole request: the 8 MiB image plus room for the multipart framing and the <c>type</c> field.</summary>
    public const long UploadRequestLimit = ImageFormats.MaxBytes + 64 * 1024;

    /// <summary>
    /// B14: once the first real box art replaced the placeholder cover (and the editor released the job lock), a deep
    /// check of that game clears its <c>DefaultBoxArt</c> issue. G1: an external game's check would re-hash every external
    /// file, so its issue is resolved directly under the job lock instead. Skipped, never queued, when another job holds
    /// the lock; the response is the game detail whatever happens here.
    /// </summary>
    private static async Task StartGameCheckAsync(LibraryEditResult result, Guid id, HttpContext ctx, IGameRepository repository,
        JobRunner runner, IntegrityJobFactory integrity, ILoggerFactory loggers, ISystemRegistry systems, JobLock jobLock,
        IIntegrityIssueRepository issues, TimeProvider time)
    {
        if (result.Code is not null || !result.ReplacedDefaultCover)
            return;
        var logger = loggers.CreateLogger("RnRomManager.Api.Endpoints.GameCheck");
        try
        {
            if (await repository.GetAsync(id, CancellationToken.None) is not { } row)
                return;
            if (systems.ById(row.SystemId) is { EmbedRomsInZip: false })
            {
                using var held = jobLock.TryAcquire("Saving changes");
                if (held is null)
                    logger.LogInformation("Resolving DefaultBoxArt of {ZipPath} skipped: another job is running", row.ZipPath);
                else
                    await issues.ResolveOpenAsync(row.ZipPath, IntegrityIssueType.DefaultBoxArt, time.GetUtcNow().UtcDateTime, CancellationToken.None);
                return;
            }
            var started = await runner.StartAsync(integrity(JobMode.Game, row.ZipPath), JobTrigger.Manual, DatEndpoints.UserId(ctx.User),
                CancellationToken.None);
            if (started is null)
                logger.LogInformation("Game check of {ZipPath} skipped: another job is running", row.ZipPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Game check of game {GameId} could not start", id);
        }
    }

    private static IResult TooLarge(HttpContext ctx) =>
        Problems.Create(ctx, StatusCodes.Status413PayloadTooLarge, "PayloadTooLarge", "The upload is larger than 8 MiB.");

    /// <summary>
    /// The multipart form of an image upload (I5, B8), or the problem to answer: 413 when it's too large, 400 <c>file</c>
    /// when it isn't a form. Endpoints using it also carry <c>RequestSizeLimit(UploadRequestLimit)</c>.
    /// </summary>
    internal static async Task<(IFormCollection? Form, IResult? Problem)> ReadImageFormAsync(HttpContext ctx)
    {
        // Kestrel enforces the size-limit metadata; this also covers hosts that don't (TestServer) and
        // chunked bodies, which only fail while the form is read.
        if (ctx.Request.ContentLength > UploadRequestLimit)
            return (null, TooLarge(ctx));
        if (!ctx.Request.HasFormContentType)
            return (null, Problems.Validation(ctx, new Dictionary<string, string[]> { ["file"] = ["Send the image as multipart/form-data."] }));
        try
        {
            return (await ctx.Request.ReadFormAsync(ctx.RequestAborted), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            return (null, TooLarge(ctx));
        }
    }

    /// <summary>The form's <c>file</c> (≤ 8 MiB; format and dimensions are checked later), or null with an error added.</summary>
    internal static async Task<byte[]?> ReadImageFileAsync(IFormCollection form, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            errors["file"] = ["Choose an image file."];
        else if (file.Length > ImageFormats.MaxBytes)
            errors["file"] = ["The image must be at most 8 MiB."];
        if (errors.ContainsKey("file"))
            return null;

        using var buffer = new MemoryStream((int)file!.Length);
        await file.CopyToAsync(buffer, cancellationToken);
        return ImageFormats.ConvertWebp(buffer.ToArray());
    }

    /// <summary>U2/B9: at most 1024 characters (<c>images.source_url</c>), absolute http(s), no user info; else null.</summary>
    public static Uri? ParseImageUrl(string? text) =>
        text is { Length: > 0 and <= 1024 } && Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https" && parsed.UserInfo.Length == 0
                ? parsed
                : null;

    /// <summary>U3/B9: the <c>url</c> error for downloaded bytes, or null when they're a storable PNG/JPEG.</summary>
    public static string? DownloadedImageError(byte[] bytes) =>
        bytes.Length > ImageFormats.MaxBytes ? "The image must be at most 8 MiB."
        : bytes.Length == 0 || ImageFormats.Detect(bytes) is null || ImageFormats.Read(bytes) is null ? "The URL didn't return a PNG or JPEG image."
        : null;

    private static async Task<IResult> DetailAsync(Guid id, HttpContext ctx, IGameRepository repository, IRomRepository roms, IImageRepository images)
    {
        if (await repository.GetAsync(id, ctx.RequestAborted) is not { } game)
            return Problems.NotFound(ctx);
        if (SiteUserName(ctx) is { } username)
            game = game with
            {
                SaveCount = (await repository.GetUserSaveCountsAsync(username, [id], ctx.RequestAborted)).GetValueOrDefault(id),
            };
        var romRows = await roms.ListByGameAsync(id, ctx.RequestAborted);
        var imageRows = await images.ListByGameAsync(id, ctx.RequestAborted);
        var handler = ctx.RequestServices.GetRequiredService<ISystemRegistry>().ById(game.SystemId);
        var systemName = handler?.DisplayName ?? game.SystemId;
        var notes = ctx.RequestServices.GetRequiredService<LibraryFiles>().NoteEntries(game);
        var fileRows = (await roms.ListFilesByGameAsync(id, ctx.RequestAborted)).ToLookup(f => f.RomId);
        // Multi-disc M3 (Saturn/Sega CD S2, Amiga F3, Plus/4 B11): PlayStation, Saturn, Sega CD, Amiga and Plus/4; other systems have none. Keyed by each set's Disc 1.
        var discSets = DiscSets.Find(game.SystemId, romRows, fileRows).ToDictionary(s => s.Discs[0].Id);
        var igdbConfigured = SourcesOptions.IgdbConfigured(ctx.RequestServices.GetRequiredService<IOptions<SourcesOptions>>().Value);
        var actions = await repository.GetActionsAsync(id, ctx.RequestAborted);
        return Results.Ok(ToDetail(game, systemName, handler?.BrowserCore, handler?.FileSets ?? false, romRows, fileRows, imageRows, notes, discSets,
            igdbConfigured, actions));
    }

    private static IResult ToResult(HttpContext ctx, LibraryEditResult result) =>
        result.Code is null ? Results.NoContent() : ToProblem(ctx, result);

    /// <summary>§23 mutation bullet: 409 for the lock and unwritable ZIPs, 404 for a stale id, 500 for write failures.</summary>
    internal static IResult ToProblem(HttpContext ctx, LibraryEditResult result, IReadOnlyDictionary<string, object?>? extensions = null) =>
        result.Code switch
        {
            LibraryEditResult.ValidationFailed => Problems.Validation(ctx, result.Errors!, extensions),
            LibraryEditResult.NotFound => Problems.NotFound(ctx, extensions),
            LibraryEditResult.JobRunning or LibraryEditResult.TooManyImages or LibraryFiles.NoteTooLarge or LibraryEditResult.DuplicateImage or LibraryEditResult.DefaultImage
                or LibraryTarget.InfoJsonNewerSchema or LibraryTarget.TargetZipUnreadable or LibraryEditResult.NameTaken =>
                Problems.Create(ctx, StatusCodes.Status409Conflict, result.Code, result.Code, extensions),
            LibraryEditResult.StorageMigrationPending or LibraryEditResult.SaveLimitReached or LibraryEditResult.SavesUnreadable
                or LibraryEditResult.SavesIndexFull =>
                // § API: these codes carry a sentence for the SPA to show as the title.
                Problems.Create(ctx, StatusCodes.Status409Conflict, result.Code, result.Code switch
                {
                    LibraryEditResult.StorageMigrationPending => LibraryEditResult.StorageMigrationPendingTitle,
                    LibraryEditResult.SaveLimitReached => LibraryEditResult.SaveLimitReachedTitle,
                    LibraryEditResult.SavesUnreadable => LibraryEditResult.SavesUnreadableTitle,
                    _ => LibraryEditResult.SavesIndexFullTitle,
                }, extensions),
            _ => Problems.Create(ctx, StatusCodes.Status500InternalServerError, result.Code!, result.Code!, extensions),
        };

    private const string TitleRule = "Must contain 1–255 characters and produce a nonempty filename.";

    /// <summary>§6.1 game title: 1–255 chars after trim, must not sanitize to empty.</summary>
    private static bool IsValidTitle(string? trimmed) =>
        trimmed is { Length: >= 1 and <= 255 } && FileNameSanitizer.Sanitize(trimmed).Length > 0;

    /// <summary>
    /// U2: <c>url</c> required, at most 1024 characters (<c>images.source_url</c>), absolute http(s) without user info;
    /// <c>type</c> exactly <c>BoxArt</c> or <c>Snap</c>, as for the upload. Nothing is downloaded when this fails.
    /// </summary>
    public static Dictionary<string, string[]> Validate(ImageUrlRequest request, out Uri? url, out ImageType type)
    {
        var errors = new Dictionary<string, string[]>();
        url = ParseImageUrl(request.Url);
        if (url is null)
            errors["url"] = ["Enter an http or https image URL."];
        ImageType? parsedType = request.Type switch { "BoxArt" => ImageType.BoxArt, "Snap" => ImageType.Snap, _ => null };
        if (parsedType is null)
            errors["type"] = ["Choose BoxArt or Snap."];
        type = parsedType ?? default;
        return errors;
    }

    /// <summary><c>move</c> body (§23): <c>{targetGameId}</c> or <c>{newGameTitle}</c>, not both.</summary>
    public static Dictionary<string, string[]> Validate(MoveReleaseRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var hasTarget = request.TargetGameId is { } target && target != Guid.Empty;
        if (hasTarget == request.NewGameTitle is not null)
            errors["targetGameId"] = ["Give either targetGameId or newGameTitle."];
        else if (request.NewGameTitle is not null && !IsValidTitle(request.NewGameTitle.Trim()))
            errors["newGameTitle"] = [TitleRule];
        return errors;
    }

    /// <summary>
    /// B5: <c>{text: string}</c>, line endings normalised to <c>\n</c>; whitespace-only becomes <c>""</c> (delete);
    /// more than <see cref="LibraryZip.MaxNoteBytes"/> UTF-8 bytes after normalisation is an error.
    /// </summary>
    public static (string? Text, Dictionary<string, string[]> Errors) ParseNote(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
        {
            errors["text"] = ["A text string is required."];
            return (null, errors);
        }
        var text = value.GetString()!.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(text))
            return ("", errors);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > LibraryZip.MaxNoteBytes)
            errors["text"] = [$"The note must be at most {LibraryZip.MaxNoteBytes:N0} bytes (UTF-8)."];
        return (text, errors);
    }

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
            if (!IsValidTitle(value))
                errors["title"] = [TitleRule];
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

        if (body.TryGetProperty("igdbId", out var igdbId))
        {
            if (igdbId.ValueKind == JsonValueKind.Null)
                patch = patch with { SetIgdbId = true };
            else if (igdbId.ValueKind == JsonValueKind.Number && igdbId.TryGetInt32(out var value) && value >= 0)
                patch = patch with { SetIgdbId = true, IgdbId = value };
            else
                errors["igdbId"] = ["Must be null, 0 or a positive whole number."];
        }
        return (patch, errors);
    }

    private static readonly HashSet<string> ReleasePatchFields =
        ["kind", "regions", "languages", "revision", "disc", "hack", "translated", "version"];

    /// <summary>
    /// Release editing B4: <c>PATCH /api/games/{id}/roms/{romId}</c> body. Every property is optional (absent = unchanged);
    /// <c>revision</c>, <c>disc</c> and <c>version</c> may be null to clear them. The checks that need the game run in
    /// <see cref="LibraryEditor.EditReleaseAsync"/>.
    /// </summary>
    public static (ReleaseEdit Patch, Dictionary<string, string[]> Errors) ParseReleasePatch(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        var patch = new ReleaseEdit();
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors[""] = ["A JSON object is required."];
            return (patch, errors);
        }
        foreach (var property in body.EnumerateObject().Where(p => !ReleasePatchFields.Contains(p.Name)))
            errors[property.Name] = ["Unknown field."];

        if (body.TryGetProperty("kind", out var kind))
        {
            var value = kind.ValueKind == JsonValueKind.String ? kind.GetString() : null;
            if (value is ReleaseEdit.KindRelease or ReleaseEdit.KindUpdate or ReleaseEdit.KindDlc)
                patch = patch with { Kind = value };
            else
                errors["kind"] = ["Must be release, update or dlc."];
        }
        if (body.TryGetProperty("regions", out var regions))
        {
            var items = Strings(regions)?.Distinct(StringComparer.Ordinal).ToList();
            if (items is null || items.Count > 10 || items.Any(i => i.Length == 0))
                errors["regions"] = ["Must be known No-Intro regions."];
            else
                patch = patch with { Regions = items };
        }
        if (body.TryGetProperty("languages", out var languages))
        {
            var items = Strings(languages)?.Select(l => l.Trim()).Distinct(StringComparer.Ordinal).ToList();
            if (items is null || items.Count > 20 || items.Any(l => l.Length is < 1 or > 16 || !l.All(c => char.IsAsciiLetter(c) || c == '-')))
                errors["languages"] = ["Must be 1–16 letters or '-' each."];
            else
                patch = patch with { Languages = items };
        }
        if (body.TryGetProperty("revision", out var revision))
        {
            var value = revision.ValueKind == JsonValueKind.String ? revision.GetString()!.Trim() : null;
            if (revision.ValueKind == JsonValueKind.Null || value?.Length == 0)
                patch = patch with { SetRevision = true };
            else if (value is { Length: <= 32 } && value.IndexOfAny(['(', ')']) < 0)
                patch = patch with { SetRevision = true, Revision = value };
            else
                errors["revision"] = ["Must be at most 32 characters without parentheses."];
        }
        if (body.TryGetProperty("disc", out var disc))
        {
            if (disc.ValueKind == JsonValueKind.Null)
                patch = patch with { SetDisc = true };
            else if (disc.ValueKind == JsonValueKind.Number && disc.TryGetInt32(out var value) && value is >= 1 and <= 99)
                patch = patch with { SetDisc = true, Disc = value };
            else
                errors["disc"] = ["Must be 1–99."];
        }
        patch = patch with { Hack = Bool(body, "hack", errors), Translated = Bool(body, "translated", errors) };
        if (body.TryGetProperty("version", out var version))
        {
            if (version.ValueKind == JsonValueKind.Null)
                patch = patch with { SetVersion = true };
            else if (version.ValueKind == JsonValueKind.String && ReleaseVersion.Valid().IsMatch(version.GetString()!))
                patch = patch with { SetVersion = true, Version = version.GetString() };
            else
                errors["version"] = ["Must be like 1.0.1."];
        }
        return (patch, errors);

        static List<string>? Strings(JsonElement list) =>
            list.ValueKind == JsonValueKind.Array && list.EnumerateArray().All(i => i.ValueKind == JsonValueKind.String)
                ? [.. list.EnumerateArray().Select(i => i.GetString()!)]
                : null;

        static bool? Bool(JsonElement body, string field, Dictionary<string, string[]> errors)
        {
            if (!body.TryGetProperty(field, out var value))
                return null;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
            errors[field] = ["Must be true or false."];
            return null;
        }
    }

    /// <summary>
    /// <c>PATCH /api/games/{id}/rating</c> body: <c>{rating?: 1–5 | null, favourite?: bool}</c>, at least one present;
    /// an absent property is unchanged, <c>rating: null</c> clears it.
    /// </summary>
    public static (GamePatch Patch, Dictionary<string, string[]> Errors) ParseRating(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        var patch = new GamePatch();
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors[""] = ["A JSON object is required."];
            return (patch, errors);
        }

        var hasRating = body.TryGetProperty("rating", out var rating);
        var hasFavourite = body.TryGetProperty("favourite", out var favourite);
        if (!hasRating && !hasFavourite)
            errors[""] = ["Give rating, favourite or both."];

        if (hasRating)
        {
            if (rating.ValueKind == JsonValueKind.Null)
                patch = patch with { SetRating = true };
            else if (rating.ValueKind == JsonValueKind.Number && rating.TryGetInt32(out var value) && value is >= 1 and <= 5)
                patch = patch with { SetRating = true, Rating = value };
            else
                errors["rating"] = ["Must be null or a whole number 1–5."];
        }

        if (hasFavourite)
        {
            if (favourite.ValueKind is JsonValueKind.True or JsonValueKind.False)
                patch = patch with { Favourite = favourite.GetBoolean() };
            else
                errors["favourite"] = ["Must be true or false."];
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
        string? q, string? sort, string? dir, int? page, int? pageSize, ISystemRegistry systems, string? favourite = null,
        string? collection = null, string? notInCollection = null)
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
        else if (new GameQuery { Search = q }.Words.Count > GameQuery.MaxWords)
            errors["q"] = [$"At most {GameQuery.MaxWords} words."];

        GameSort gameSort = GameSort.Title;
        switch (sort)
        {
            case null or "title": break;
            case "year": gameSort = GameSort.Year; break;
            case "added": gameSort = GameSort.Added; break;
            case "rating": gameSort = GameSort.Rating; break;
            case "relevance": gameSort = GameSort.Relevance; break;
            default: errors["sort"] = ["Must be title, year, added, rating or relevance."]; break;
        }
        if (dir is not (null or "asc" or "desc"))
            errors["dir"] = ["Must be asc or desc."];

        bool? onlyFavourite = null;
        switch (favourite)
        {
            case null: break;
            case "true": onlyFavourite = true; break;
            case "false": onlyFavourite = false; break;
            default: errors["favourite"] = ["Must be true or false."]; break;
        }
        var collectionId = ParseCollection(collection, "collection", errors);
        var notInCollectionId = ParseCollection(notInCollection, "notInCollection", errors);
        var query = new GameQuery
        {
            System = system,
            Letter = letter,
            Region = region,
            Search = q,
            Favourite = onlyFavourite,
            CollectionId = collectionId,
            NotInCollectionId = notInCollectionId,
            Sort = gameSort,
            // §23: asc for title/year, desc for added (to the library, or to the collection: B15) and rating; relevance ignores it.
            Descending = gameSort != GameSort.Relevance && (dir is null ? gameSort is GameSort.Added or GameSort.Rating : dir == "desc"),
            Page = page ?? 1,
            PageSize = pageSize ?? Page<GameListItem>.DefaultSize,
        };
        return (query, errors);
    }

    private static Guid? ParseCollection(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null)
            return null;
        if (Guid.TryParse(value, out var id))
            return id;
        errors[field] = ["Must be a collection id."];
        return null;
    }

    private const string CoverCacheControl = "private, max-age=31536000, immutable";

    internal static string ImageUrl(Guid gameId, long imageId) => $"/api/games/{gameId}/images/{imageId}";

    /// <summary>C4: the cached WebP of the game's cover.</summary>
    internal static string CoverUrl(Guid gameId, string coverSha1) => $"/api/games/{gameId}/cover/{coverSha1}";

    /// <summary>One <c>/api/games</c> row (spec §23), also used by discovery group pages.</summary>
    /// <summary>S7: a Site user's name (their saves are counted apart); null for an Admin, who sees every save.</summary>
    internal static string? SiteUserName(HttpContext ctx) =>
        ctx.User.IsInRole(Roles.Admin) ? null : ctx.User.Identity?.Name;

    /// <summary>S7: for a Site user, each game's <c>saveCount</c> becomes their own count (0 without a row).</summary>
    internal static async Task<Page<GameListItem>> ForViewerAsync(Page<GameListItem> page, HttpContext ctx, IGameRepository repository)
    {
        if (SiteUserName(ctx) is not { } username)
            return page;
        var counts = await repository.GetUserSaveCountsAsync(username, page.Items.Select(g => g.Id).ToList(), ctx.RequestAborted);
        return page with { Items = page.Items.Select(g => g with { SaveCount = counts.GetValueOrDefault(g.Id) }).ToList() };
    }

    /// <summary>
    /// Virtual Jaguar plays a file only from cart space. An .abs, .cof or .jag program loads into RAM, which the EmulatorJS
    /// build's reset wipes after loading; a .j64/.rom is placed by size alone: a cart (a whole number of MiB, or 128 KiB)
    /// or an Alpine image (8 KiB short of one). Mirrors the SPA's <c>jaguarRomLoads</c>.
    /// </summary>
    public static bool JaguarRomLoads(string fileName, long sizeBytes)
    {
        if (Path.GetExtension(fileName).ToLowerInvariant() is ".abs" or ".cof" or ".jag")
            return false;
        const long mib = 1048576;
        return sizeBytes % mib == 0 || sizeBytes == 131072 || (sizeBytes + 8192) % mib == 0;
    }

    /// <summary>
    /// 3DS P2: azahar plays a <c>.3ds</c> or <c>.cci</c> whose header doesn't say it's encrypted (a missing header or field
    /// counts as playable); its libretro build can't load a <c>.cia</c>. Mirrors the SPA's <c>azaharRomLoads</c>.
    /// </summary>
    public static bool AzaharRomLoads(string fileName, bool encrypted) =>
        !encrypted && Path.GetExtension(fileName).ToLowerInvariant() is ".3ds" or ".cci";

    internal static object ToListItemDto(GameListItem game, ISystemRegistry systems)
    {
        // C2: quick play needs a browser core and a primary release stored in the zip; Play B1 / PSP B1:
        // PlayStation and PSP play their external primary disc through play-disc (a Saturn .nrg too, N5).
        var core = systems.ById(game.SystemId)?.BrowserCore;
        var playable = core is not null && game.PrimaryRomId is not null
            && (!game.PrimaryIsExternal || ExternalBrowserCores.Contains(core))
            // TurboGrafx-CD B5: Beetle PCE runs a lone .bin as a HuCard, and (PC-FX X6) Beetle PC-FX can't open one, so only a cue set's .bin plays.
            && !(core is "mednafen_pce" or "mednafen_pcfx" && game.PrimaryIsExternal && !game.PrimaryHasFiles
                 && game.PrimaryRomFileName?.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) == true)
            // Amiga: PUAE's WebAssembly build has no capsimg, so an .ipf never plays.
            && !(core == "puae" && game.PrimaryRomFileName?.EndsWith(".ipf", StringComparison.OrdinalIgnoreCase) == true)
            && !(core == "virtualjaguar" && game.PrimaryRomFileName is { } name && game.PrimarySizeBytes is { } size
                 && !JaguarRomLoads(name, size))
            && !(core == "azahar" && game.PrimaryRomFileName is { } file && !AzaharRomLoads(file, game.PrimaryEncrypted));
        return new
        {
            id = game.Id,
            system = game.SystemId,
            systemName = systems.ById(game.SystemId)?.DisplayName ?? game.SystemId,
            systemShortName = systems.ById(game.SystemId)?.ShortName ?? game.SystemId,
            displayTitle = game.DisplayTitle,
            releaseYear = game.ReleaseYear,
            regions = game.Regions,
            romCount = game.RomCount,
            primarySizeBytes = game.PrimarySizeBytes,
            boxArtUrl = game.BoxArtSha1 is { } sha1 ? CoverUrl(game.Id, sha1) : null,
            rating = game.Rating,
            favourite = game.Favourite,
            matchedOn = game.MatchedOn is { } match ? new { field = match.Field, value = match.Value } : null,
            updateCount = game.UpdateCount,
            dlcCount = game.DlcCount,
            saveCount = game.SaveCount,
            playRomId = playable ? game.PrimaryRomId : null,
            playRomFileName = playable ? game.PrimaryRomFileName : null,
            // Sega CD B6: quick play adds disc=1 for an external primary, since a shared core can't tell on its own.
            playDisc = playable && game.PrimaryIsExternal && PlayDiscCores.Contains(core!),
            browserCore = core,
            igdbId = game.IgdbId,
        };
    }

    private static object ToDetail(GameRow game, string systemName, string? browserCore, bool fileSets, IReadOnlyList<RomRow> roms, ILookup<long, RomFileRow> files,
        IReadOnlyList<ImageRow> images, IReadOnlySet<string> notes, IReadOnlyDictionary<long, DiscSet> discSets, bool igdbConfigured, GameActionsRow? actions) => new
    {
        id = game.Id,
        system = game.SystemId,
        systemName,
        browserCore,
        fileSets,
        title = game.Title,
        displayTitle = game.DisplayTitle,
        releaseYear = game.ReleaseYear,
        developers = game.Developers,
        publishers = game.Publishers,
        genres = game.Genres,
        description = game.Description,
        hasDefaultBoxArt = game.HasDefaultBoxArt,
        rating = game.Rating,
        favourite = game.Favourite,
        saveCount = game.SaveCount,
        igdbId = game.IgdbId,
        igdbConfigured,
        // R4: null unless a ratings refresh stored a score.
        igdbRating = actions?.IgdbRating is { } score
            ? new
            {
                score,
                votes = actions.IgdbRatingCount ?? 0,
                syncedAt = actions.LastIgdbSyncAt is { } synced ? DateTime.SpecifyKind(synced, DateTimeKind.Utc) : (DateTime?)null,
            }
            : null,
        metadata = new { createdAt = game.CreatedAt, updatedAt = game.UpdatedAt },
        roms = roms.OrderByDescending(r => r.IsPrimary).ThenBy(r => r.FileName, StringComparer.Ordinal).Select(r => new
        {
            id = r.Id,
            fileName = r.FileName,
            externalFileName = r.ExternalFileName,
            originalFileName = r.OriginalFileName,
            baseTitle = r.BaseTitle,
            regions = r.Regions,
            languages = r.Languages,
            revision = r.Revision,
            version = r.Version,
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
            // B20: a set's other files in order (null without), and the release's size with them.
            files = files.Contains(r.Id)
                ? files[r.Id].OrderBy(f => f.Ordinal).Select(f => new
                {
                    id = f.Id,
                    fileName = f.FileName,
                    sizeBytes = f.SizeBytes,
                    crc32 = f.Crc32,
                    md5 = f.Md5,
                    sha1 = f.Sha1,
                    downloadUrl = $"/api/games/{game.Id}/roms/{r.Id}/files/{f.Id}/download",
                }).ToList()
                : null,
            totalSizeBytes = r.SizeBytes + files[r.Id].Sum(f => f.SizeBytes),
            hasNote = notes.Contains(LibraryZip.NoteEntry(r.FileName)),
            discSet = discSets.GetValueOrDefault(r.Id) is { } set ? new { name = set.Name, romIds = set.Discs.Select(d => d.Id).ToList() } : null,
        }),
        images = images.Select(i => new
        {
            id = i.Id,
            type = i.Type,
            fileName = i.FileName,
            source = i.Source,
            isDefault = i.IsDefault,
            isCover = i.IsCover,
            sizeBytes = i.SizeBytes,
            width = i.Width,
            height = i.Height,
            url = ImageUrl(game.Id, i.Id),
        }),
    };
}

/// <summary>U1 body: <c>{ "url": "…", "type": "BoxArt" | "Snap" }</c>.</summary>
public sealed record ImageUrlRequest(string? Url, string? Type);
