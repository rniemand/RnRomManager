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
            var (query, errors) = LibraryRequestParsing.ParseQuery(system, letter, region, q, sort, dir, page, pageSize, systems, favourite, collection, notInCollection);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            // B15/P1: another user's collection answers 404 like a missing one, before any membership is queried.
            foreach (var collectionId in new[] { query.CollectionId, query.NotInCollectionId })
                if (collectionId is { } id && await collections.GetAsync(id, EndpointUser.RequiredId(ctx), ctx.RequestAborted) is null)
                    return Problems.NotFound(ctx);
            var result = await ForViewerAsync(await repository.ListAsync(query, ctx.RequestAborted), ctx, repository);
            return Results.Ok(Paging.ToDto(result, game => LibraryDtos.ToListItemDto(game, systems)));
        });

        // F1: per-option counts for the Library filters; each facet ignores its own filter. Sort, paging and collections aren't read.
        games.MapGet("/facets", async (string? system, string? letter, string? region, string? q, string? favourite, HttpContext ctx,
            ISystemRegistry systems, IGameRepository repository) =>
        {
            var (query, errors) = LibraryRequestParsing.ParseQuery(system, letter, region, q, null, null, null, null, systems, favourite);
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

        api.MapLibraryImageEndpoints();

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
                return Problems.Validation(ctx, "path", "Required.");
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
            var list = await collections.ListForGameAsync(id, EndpointUser.RequiredId(ctx), ctx.RequestAborted);
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
            var (text, errors) = LibraryRequestParsing.ParseNote(body);
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
            var (patch, errors) = LibraryRequestParsing.ParsePatch(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.PatchAsync(id, patch, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? await DetailAsync(id, ctx, repository, roms, images) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // Admin-only (U3; shared per game); takes the job lock and rewrites the ZIP like every edit.
        games.MapPatch("/{id:guid}/rating", async (Guid id, JsonElement body, HttpContext ctx, LibraryEditor editor, IGameRepository repository,
            IRomRepository roms, IImageRepository images) =>
        {
            var (patch, errors) = LibraryRequestParsing.ParseRating(body);
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
            var (patch, errors) = LibraryRequestParsing.ParseReleasePatch(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            var result = await editor.EditReleaseAsync(id, romId, patch, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? Results.Ok(new { fileName = result.FileName }) : ToProblem(ctx, result);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapPost("/{id:guid}/roms/{romId:long}/move", async (Guid id, long romId, MoveReleaseRequest request, HttpContext ctx,
            LibraryEditor editor) =>
        {
            var errors = LibraryRequestParsing.Validate(request);
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

        // L1/L2: read-only, so no job lock and no audit row.
        games.MapGet("/{id:guid}/igdb-matches", async (Guid id, HttpContext ctx, IGameRepository repository, IGameInfoLookup lookup) =>
        {
            string? q = null;
            if (ctx.Request.Query.TryGetValue("q", out var values))
            {
                q = values.ToString().Trim();
                if (q.Length is 0 or > MaxLookupQuery || IgdbClient.SearchTitle(q).Length == 0)
                    return Problems.Validation(ctx, "q", "Must be 1–100 characters.");
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

        api.MapPost("/jobs/media-retry", async (HttpContext ctx, JobRunner runner, MediaRetryJob job) =>
        {
            return await JobStart.StartManualAsync(runner, job, ctx, ctx.RequestAborted);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // Admin actions R1.
        api.MapPost("/jobs/igdb-sync", async (HttpContext ctx, JobRunner runner, IgdbSyncJob job, IIgdbGames igdb) =>
        {
            if (!igdb.Configured)
                return Problems.Create(ctx, StatusCodes.Status409Conflict, IgdbNotConfigured,
                    "Refreshing ratings needs Sources:IgdbClientId and Sources:IgdbClientSecret.");
            return await JobStart.StartManualAsync(runner, job, ctx, ctx.RequestAborted);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        // Admin actions P1.
        api.MapPost("/jobs/pull-covers", async (HttpContext ctx, JobRunner runner, PullCoversJob job, IIgdbGames igdb) =>
        {
            if (!igdb.Configured)
                return Problems.Create(ctx, StatusCodes.Status409Conflict, IgdbNotConfigured,
                    "Pulling covers needs Sources:IgdbClientId and Sources:IgdbClientSecret.");
            return await JobStart.StartManualAsync(runner, job, ctx, ctx.RequestAborted);
        }).RequireAuthorization(AuthSetup.AdminOnly);

        games.MapPost("/{id:guid}/merge", async (Guid id, MergeRequest request, HttpContext ctx, LibraryEditor editor) =>
        {
            if (request.TargetGameId is not { } target || target == Guid.Empty)
                return Problems.Validation(ctx, "targetGameId", "A target game is required.");
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

    internal static async Task<IResult> DetailAsync(
        Guid id,
        HttpContext ctx,
        IGameRepository repository,
        IRomRepository roms,
        IImageRepository images)
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
        return Results.Ok(LibraryDtos.ToDetail(game, systemName, handler?.BrowserCore, handler?.FileSets ?? false, romRows, fileRows, imageRows, notes, discSets,
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

    /// <summary>One <c>/api/games</c> row (spec §23), also used by discovery group pages.</summary>
    /// <summary>S7: a Site user's name (their saves are counted apart); null for an Admin, who sees every save.</summary>
    internal static string? SiteUserName(HttpContext ctx) =>
        ctx.User.IsInRole(Roles.Admin) ? null : ctx.User.Identity?.Name;

    /// <summary>
    /// S1: each game's <c>ownSaveCount</c> is the viewer's own saves (Admins too); S7: for a Site user <c>saveCount</c>
    /// becomes that count as well (0 without a row).
    /// </summary>
    internal static async Task<Page<GameListItem>> ForViewerAsync(Page<GameListItem> page, HttpContext ctx, IGameRepository repository)
    {
        if (ctx.User.Identity?.Name is not { } username)
            return page;
        var counts = await repository.GetUserSaveCountsAsync(username, page.Items.Select(g => g.Id).ToList(), ctx.RequestAborted);
        var site = SiteUserName(ctx) is not null;
        return page with
        {
            Items = page.Items.Select(g => counts.GetValueOrDefault(g.Id) is var own
                ? g with { OwnSaveCount = own, SaveCount = site ? own : g.SaveCount }
                : g).ToList(),
        };
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
}
