using System.Text.Json;
using RnRomManager.Api.Auth;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// B7–B12: a game's saves, for any signed-in user (the <c>/api</c> group's session and antiforgery, no extra policy),
/// like the note routes. Writes go through <see cref="LibraryEditor"/>, which takes the job lock.
/// </summary>
public static class SaveEndpoints
{
    /// <summary>B8: the whole upload request: a 256 MiB save plus 64 KiB for the framing and the text fields.</summary>
    public const long UploadRequestLimit = SaveUploadReader.RequestLimit;

    private static readonly HashSet<string> AddFields = new(StringComparer.Ordinal) { "romId", "kind", "name", "description" };

    public static RouteGroupBuilder MapSaveEndpoints(this RouteGroupBuilder api)
    {
        var saves = api.MapGroup("/games/{id:guid}/saves");

        // B8: Content-Length 413 first; text fields; 400 validation; the cheap 404/409 checks; then the file is streamed.
        saves.MapPost("", async (Guid id, HttpContext ctx, LibraryEditor editor, IRomRepository roms, IOptions<PathsOptions> paths) =>
        {
            var (form, problem) = await SaveUploadReader.ReadFieldsAsync(ctx, AddFields);
            if (problem is not null)
                return problem;
            using var upload = form!;
            var fields = upload.Fields;
            var errors = new Dictionary<string, string[]>();
            if (!long.TryParse(fields.GetValueOrDefault("romId"), System.Globalization.NumberStyles.None, null, out var romId) || romId <= 0)
                errors["romId"] = ["Choose a release of this game."];
            var kind = fields.GetValueOrDefault("kind");
            if (!GameSaves.IsKind(kind))
                errors["kind"] = ["Choose State or Battery."];
            if (GameSaves.NameProblem(fields.GetValueOrDefault("name"), out var name) is { } nameProblem)
                errors["name"] = [nameProblem];
            var description = fields.GetValueOrDefault("description");
            if (GameSaves.DescriptionProblem(description) is { } descriptionProblem)
                errors["description"] = [descriptionProblem];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            if (await editor.PrecheckSaveAsync(id, romId, null, ctx.RequestAborted) is { } refused)
                return LibraryEndpoints.ToProblem(ctx, refused);

            var (file, fileProblem) = await upload.ReadFileAsync(ctx, paths);
            if (fileProblem is not null)
                return fileProblem;
            var result = await editor.AddSaveAsync(id, romId, kind!, name, description, file!, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null
                ? Results.Created($"/api/games/{id}/saves/{result.Save!.Id}", ToItem(result.Save, await roms.ListByGameAsync(id, ctx.RequestAborted)))
                : LibraryEndpoints.ToProblem(ctx, result);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(UploadRequestLimit));

        // B10: one file part only; the entry is renamed when the extension changes.
        saves.MapPut("/{saveId}/file", async (Guid id, string saveId, HttpContext ctx, LibraryEditor editor, IRomRepository roms,
            IOptions<PathsOptions> paths) =>
        {
            var (form, problem) = await SaveUploadReader.ReadFieldsAsync(ctx, new HashSet<string>());
            if (problem is not null)
                return problem;
            using var upload = form!;
            if (await editor.PrecheckSaveAsync(id, null, saveId, ctx.RequestAborted) is { } refused)
                return LibraryEndpoints.ToProblem(ctx, refused);
            var (file, fileProblem) = await upload.ReadFileAsync(ctx, paths);
            if (fileProblem is not null)
                return fileProblem;
            var result = await editor.ReplaceSaveFileAsync(id, saveId, file!, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null
                ? Results.Ok(ToItem(result.Save!, await roms.ListByGameAsync(id, ctx.RequestAborted)))
                : LibraryEndpoints.ToProblem(ctx, result);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(UploadRequestLimit));

        saves.MapGet("", async (Guid id, HttpContext ctx, IGameRepository games, IRomRepository roms, LibraryFiles files) =>
        {
            if (await games.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            if (files.ReadSaves(ctx, game, out var read) is { } problem)
                return problem;
            if (read!.State == SavesState.Unreadable)
                return LibraryEndpoints.ToProblem(ctx, new LibraryEditResult(LibraryEditResult.SavesUnreadable));
            var releases = await roms.ListByGameAsync(id, ctx.RequestAborted);
            return Results.Ok(new
            {
                items = (read.Index?.Saves ?? []).OrderByDescending(s => s.UpdatedAt).Select(s => ToItem(s, releases)),
            });
        });

        // B9: at least one of name and description; same rules as the upload.
        saves.MapPatch("/{saveId}", async (Guid id, string saveId, JsonElement body, HttpContext ctx, LibraryEditor editor, IRomRepository roms) =>
        {
            var (name, description, errors) = ParsePatch(body);
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);
            if (await editor.PrecheckSaveAsync(id, null, saveId, ctx.RequestAborted) is { } refused)
                return LibraryEndpoints.ToProblem(ctx, refused);
            var result = await editor.UpdateSaveAsync(id, saveId, name, description, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null
                ? Results.Ok(ToItem(result.Save!, await roms.ListByGameAsync(id, ctx.RequestAborted)))
                : LibraryEndpoints.ToProblem(ctx, result);
        });

        saves.MapDelete("/{saveId}", async (Guid id, string saveId, HttpContext ctx, LibraryEditor editor) =>
        {
            if (await editor.PrecheckSaveAsync(id, null, saveId, ctx.RequestAborted) is { } refused)
                return LibraryEndpoints.ToProblem(ctx, refused);
            var result = await editor.DeleteSaveAsync(id, saveId, AuditActor.From(ctx.User), ctx.RequestAborted);
            return result.Code is null ? Results.NoContent() : LibraryEndpoints.ToProblem(ctx, result);
        });

        // B12: streamed (bounded by its recorded size), named "<title> - <name><extension>".
        saves.MapGet("/{saveId}/download", async (Guid id, string saveId, HttpContext ctx, IGameRepository games, LibraryFiles files) =>
        {
            if (await games.GetAsync(id, ctx.RequestAborted) is not { } game)
                return Problems.NotFound(ctx);
            if (files.ReadSaves(ctx, game, out var read) is { } problem)
                return problem;
            if (read!.State == SavesState.Unreadable)
                return LibraryEndpoints.ToProblem(ctx, new LibraryEditResult(LibraryEditResult.SavesUnreadable));
            if (read.Index?.Saves.FirstOrDefault(s => s.Id == saveId) is not { } save)
                return Problems.NotFound(ctx);
            if (files.OpenEntry(ctx, game, save.Entry, save.SizeBytes, out var stream) is { } entryProblem)
                return entryProblem;
            return LibraryDownload.Send(ctx, new LibraryAttachment(stream!, save.SizeBytes, DownloadName(game.Title, save), ranges: false));
        });

        return api;
    }

    /// <summary>B7: one save as the API lists it; <c>romId</c> is the release its <c>release</c> names now, or null (B4).</summary>
    internal static object ToItem(GameSave save, IReadOnlyList<RomRow> releases) => new
    {
        id = save.Id,
        name = save.Name,
        description = save.Description,
        kind = save.Kind,
        releaseFileName = save.Release,
        romId = releases.FirstOrDefault(r => GameSaves.Names(save, r.FileName))?.Id,
        sizeBytes = save.SizeBytes,
        extension = save.Extension,
        uploadedBy = save.UploadedBy,
        uploadedAt = save.UploadedAt,
        updatedBy = save.UpdatedBy,
        updatedAt = save.UpdatedAt,
    };

    /// <summary>B12: <c>&lt;title&gt; - &lt;name&gt;&lt;extension&gt;</c>, each character Windows refuses in a file name replaced by <c>_</c>.</summary>
    internal static string DownloadName(string title, GameSave save) =>
        string.Concat($"{title} - {save.Name}{save.Extension}".Select(c => c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c) ? '_' : c));

    private static (string? Name, string? Description, Dictionary<string, string[]> Errors) ParsePatch(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>();
        string? Text(string field)
        {
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            errors[field] = ["Must be a string."];
            return null;
        }
        var name = Text("name");
        var description = Text("description");
        if (errors.Count > 0)
            return (null, null, errors);
        if (name is null && description is null)
            errors["name"] = ["Give a name or a description."];
        else if (name is not null && GameSaves.NameProblem(name, out _) is { } nameProblem)
            errors["name"] = [nameProblem];
        if (GameSaves.DescriptionProblem(description) is { } descriptionProblem)
            errors["description"] = [descriptionProblem];
        return (name, description, errors);
    }
}
