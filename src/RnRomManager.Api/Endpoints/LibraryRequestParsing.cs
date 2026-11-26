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

/// <summary>Query and body parsing for the <c>/api/games</c> routes (§23).</summary>
public static class LibraryRequestParsing
{
    private const string TitleRule = "Must contain 1–255 characters and produce a nonempty filename.";

    /// <summary>§6.1 game title: 1–255 chars after trim, must not sanitize to empty.</summary>
    private static bool IsValidTitle(string? trimmed) =>
        trimmed is { Length: >= 1 and <= 255 } && FileNameSanitizer.Sanitize(trimmed).Length > 0;

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
}
