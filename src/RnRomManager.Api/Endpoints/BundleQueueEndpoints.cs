using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RnRomManager.Core.Bundles;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>/api/bundle-queues*</c> (Bundle Creator B2, B4): any signed-in user, on their own saved queues only; another
/// user's queue id answers 404 like a missing one.
/// </summary>
public static class BundleQueueEndpoints
{
    /// <summary>B4: the largest snapshot or batch-resolution body (2 MiB).</summary>
    public const int RequestLimit = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapBundleQueueEndpoints(this RouteGroupBuilder api)
    {
        var queues = api.MapGroup("/bundle-queues");

        queues.MapGet("", async (HttpContext ctx, IBundleQueueRepository repository) =>
        {
            var list = await repository.ListAsync(CollectionEndpoints.UserId(ctx), ctx.RequestAborted);
            return Results.Ok(list.Select(q => new { id = q.Id, name = q.Name, revision = q.Revision, itemCount = q.ItemCount }));
        });

        queues.MapPost("", async (HttpContext ctx, IBundleQueueRepository repository, TimeProvider time) =>
        {
            var (request, problem) = await ReadJsonAsync<QueueRequest>(ctx);
            if (problem is not null)
                return problem;
            var (name, entries, errors) = Validate(request!);
            if (errors is not null)
                return Problems.Validation(ctx, errors);

            var write = await repository.CreateAsync(CollectionEndpoints.UserId(ctx), Guid.NewGuid(), name!, entries!,
                time.GetUtcNow().UtcDateTime, ctx.RequestAborted);
            return WriteResult(ctx, write, q => Results.Created($"/api/bundle-queues/{q.Id}", ToDto(q)));
        }).WithMetadata(new RequestSizeLimitAttribute(RequestLimit));

        queues.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, IBundleQueueRepository repository) =>
            await repository.GetAsync(id, CollectionEndpoints.UserId(ctx), ctx.RequestAborted) is { } queue
                ? Results.Ok(ToDto(queue))
                : Problems.NotFound(ctx));

        // B4: an atomic replace at the caller's revision; ownership, then revision, then the name are checked.
        queues.MapPut("/{id:guid}", async (Guid id, HttpContext ctx, IBundleQueueRepository repository, TimeProvider time) =>
        {
            var (request, problem) = await ReadJsonAsync<QueueRequest>(ctx);
            if (problem is not null)
                return problem;
            var (name, entries, errors) = Validate(request!);
            errors ??= [];
            var revision = request!.Revision ?? 0;
            if (revision <= 0)
                errors["revision"] = ["The queue's current revision is required."];
            if (errors.Count > 0)
                return Problems.Validation(ctx, errors);

            var write = await repository.ReplaceAsync(id, CollectionEndpoints.UserId(ctx), name!, entries!, revision,
                time.GetUtcNow().UtcDateTime, ctx.RequestAborted);
            return WriteResult(ctx, write, q => Results.Ok(ToDto(q)));
        }).WithMetadata(new RequestSizeLimitAttribute(RequestLimit));

        // B4: deletes only at the given revision; the working cart is the browser's and is never touched.
        queues.MapDelete("/{id:guid}", async (Guid id, string? revision, HttpContext ctx, IBundleQueueRepository repository) =>
        {
            if (!int.TryParse(revision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var expected)
                || expected <= 0)
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["revision"] = ["The queue's current revision is required."] });
            var status = await repository.DeleteAsync(id, CollectionEndpoints.UserId(ctx), expected, ctx.RequestAborted);
            return WriteResult(ctx, new BundleQueueWrite(status), _ => Results.NoContent());
        });

        return api;
    }

    /// <summary>The trimmed name and validated entries, or the field errors.</summary>
    internal static (string? Name, IReadOnlyList<BundleEntry>? Entries, Dictionary<string, string[]>? Errors) Validate(QueueRequest request)
    {
        var name = request.Name;
        var errors = new Dictionary<string, string[]>();
        if (BundleQueueValidation.ValidateName(ref name) is { } nameError)
            errors["name"] = [nameError];
        var (entries, entryErrors) = BundleQueueValidation.ValidateEntries(request.Entries);
        foreach (var (field, messages) in entryErrors)
            errors[field] = messages;
        return errors.Count == 0 ? (name, entries, null) : (null, null, errors);
    }

    internal static IResult WriteResult(HttpContext ctx, BundleQueueWrite write, Func<BundleQueue, IResult> ok) => write.Status switch
    {
        BundleQueueWriteStatus.Ok => ok(write.Queue!),
        BundleQueueWriteStatus.NotFound => Problems.NotFound(ctx),
        BundleQueueWriteStatus.Changed => Problems.Create(ctx, StatusCodes.Status409Conflict, "BundleQueueChanged",
            "This queue was changed elsewhere. Reload it and try again."),
        BundleQueueWriteStatus.NameTaken => Problems.Create(ctx, StatusCodes.Status409Conflict, "BundleQueueNameTaken",
            "You already have a saved queue with this name."),
        BundleQueueWriteStatus.Limit => Problems.Create(ctx, StatusCodes.Status409Conflict, "BundleQueueLimit",
            $"You can save at most {BundleQueueLimits.MaxQueuesPerUser} queues. Delete one first."),
        _ => throw new ArgumentOutOfRangeException(nameof(write)),
    };

    internal static object ToDto(BundleQueue queue) => new
    {
        id = queue.Id,
        name = queue.Name,
        revision = queue.Revision,
        entries = queue.Entries.Select(e => new
        {
            gameId = e.GameId,
            releaseSha1 = e.ReleaseSha1,
            member = e.Member is { } m ? new { sha1 = m.Sha1, fileName = m.FileName } : null,
            gameTitle = e.GameTitle,
            fileName = e.FileName,
        }),
    };

    private static IResult TooLarge(HttpContext ctx) =>
        Problems.Create(ctx, StatusCodes.Status413PayloadTooLarge, "PayloadTooLarge", "The request is larger than 2 MiB.");

    /// <summary>
    /// Buffers at most <see cref="RequestLimit"/> bytes of a JSON body (413 beyond it, fixed-length or chunked, whether or
    /// not the host enforces the size-limit metadata), then deserializes it; malformed JSON is a 400.
    /// </summary>
    internal static async Task<(T? Value, IResult? Problem)> ReadJsonAsync<T>(HttpContext ctx) where T : class
    {
        if (ctx.Request.ContentLength > RequestLimit)
            return (null, TooLarge(ctx));
        if (!ctx.Request.HasJsonContentType())
            return (null, Problems.Create(ctx, StatusCodes.Status415UnsupportedMediaType, "UnsupportedMediaType", "Send the request as application/json."));

        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted)) > 0)
            {
                if (buffer.Length + read > RequestLimit)
                    return (null, TooLarge(ctx));
                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, TooLarge(ctx));
        }

        try
        {
            buffer.Position = 0;
            if (JsonSerializer.Deserialize<T>(buffer, RequestJson) is { } value)
                return (value, null);
        }
        catch (JsonException)
        {
        }
        return (null, Problems.Validation(ctx, new Dictionary<string, string[]> { [""] = ["A valid JSON object is required."] }));
    }
}
