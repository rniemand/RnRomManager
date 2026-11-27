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

/// <summary>The response shapes of <c>/api/games</c> (§23): list rows, the game detail and image URLs.</summary>
public static class LibraryDtos
{
    internal static string ImageUrl(Guid gameId, long imageId) => $"/api/games/{gameId}/images/{imageId}";

    /// <summary>C4: the cached WebP of the game's cover.</summary>
    internal static string CoverUrl(Guid gameId, string coverSha1) => $"/api/games/{gameId}/cover/{coverSha1}";

    internal static object ToListItemDto(GameListItem game, ISystemRegistry systems)
    {
        // C2: quick play needs a browser core and a primary release stored in the zip; Play B1 / PSP B1:
        // PlayStation and PSP play their external primary disc through play-disc (a Saturn .nrg too, N5).
        var core = systems.ById(game.SystemId)?.BrowserCore;
        var playable = core is not null && game.PrimaryRomId is not null
            && (!game.PrimaryIsExternal || LibraryEndpoints.ExternalBrowserCores.Contains(core))
            // TurboGrafx-CD B5: Beetle PCE runs a lone .bin as a HuCard, and (PC-FX X6) Beetle PC-FX can't open one, so only a cue set's .bin plays, and (CD32 A10) PUAE has cue generation off, so a lone CD32/CDTV .bin never plays.
            && !(core is "mednafen_pce" or "mednafen_pcfx" or "puae" && game.PrimaryIsExternal && !game.PrimaryHasFiles
                 && game.PrimaryRomFileName?.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) == true)
            // Amiga: PUAE's WebAssembly build has no capsimg, so an .ipf never plays.
            && !(core == "puae" && game.PrimaryRomFileName?.EndsWith(".ipf", StringComparison.OrdinalIgnoreCase) == true)
            && !(core == "virtualjaguar" && game.PrimaryRomFileName is { } name && game.PrimarySizeBytes is { } size
                 && !LibraryEndpoints.JaguarRomLoads(name, size))
            && !(core == "azahar" && game.PrimaryRomFileName is { } file && !LibraryEndpoints.AzaharRomLoads(file, game.PrimaryEncrypted));
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
            ownSaveCount = game.OwnSaveCount,
            ownStateSaveCount = game.OwnStateSaveCount,
            playRomId = playable ? game.PrimaryRomId : null,
            playRomFileName = playable ? game.PrimaryRomFileName : null,
            // Sega CD B6: quick play adds disc=1 for an external primary, since a shared core can't tell on its own.
            playDisc = playable && game.PrimaryIsExternal && LibraryEndpoints.PlayDiscCores.Contains(core!),
            browserCore = core,
            igdbId = game.IgdbId,
            igdbRating = game.IgdbRating,
        };
    }

    internal static object ToDetail(
        GameRow game,
        string systemName,
        string? browserCore,
        bool fileSets,
        IReadOnlyList<RomRow> roms,
        ILookup<long, RomFileRow> files,
        IReadOnlyList<ImageRow> images,
        IReadOnlySet<string> notes,
        IReadOnlyDictionary<long, DiscSet> discSets,
        bool igdbConfigured,
        GameActionsRow? actions) => new
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
