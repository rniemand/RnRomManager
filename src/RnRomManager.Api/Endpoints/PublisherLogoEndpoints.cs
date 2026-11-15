using Microsoft.AspNetCore.Mvc;
using RnRomManager.Api.Auth;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>/api/discovery/publishers/logo?key=</c> (collections discovery B5): a publisher's shared logo override. Any
/// signed-in user reads, replaces or removes it; every change is audited. The bytes live in <c>publisher_logos</c>.
/// </summary>
public static class PublisherLogoEndpoints
{
    /// <summary>Largest stored logo.</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>Largest request body: the logo plus room for the multipart framing.</summary>
    public const int RequestLimit = 576 * 1024;

    /// <summary>Largest width and height.</summary>
    public const int MaxDimension = 2048;

    private const string Route = "/discovery/publishers/logo";

    /// <summary>B6: the override's URL for a group page, versioned by its <c>updated_at</c> so a change shows at once.</summary>
    internal static string LogoUrl(string key, DateTime updatedAt) => $"/api{Route}?key={key}&v={updatedAt.Ticks}";

    public static RouteGroupBuilder MapPublisherLogoEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(Route, async (string? key, HttpContext ctx, IPublisherLogoRepository logos) =>
        {
            if (KeyError(ctx, key) is { } invalid)
                return invalid;
            if (await logos.GetAsync(key!, ctx.RequestAborted) is not { } logo)
                return Problems.NotFound(ctx);
            ctx.Response.Headers.CacheControl = "private, no-cache";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(logo.Bytes, logo.MediaType);
        });

        api.MapPut(Route, async (string? key, HttpContext ctx, IPublisherLogoRepository logos, IDiscoveryRepository discovery,
            IAuditLog audit, TimeProvider time) =>
        {
            if (KeyError(ctx, key) is { } invalid)
                return invalid;
            var (form, problem) = await ReadFormAsync(ctx);
            if (problem is not null)
                return problem;
            var (bytes, mediaType, error) = await ReadLogoAsync(form!, ctx.RequestAborted);
            if (error is not null)
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["file"] = [error] });
            if (await discovery.GetGroupAsync(DiscoveryKind.Publishers, key!, ctx.RequestAborted) is not { } publisher)
                return Problems.NotFound(ctx);

            var actor = AuditActor.From(ctx.User);
            await logos.UpsertAsync(new PublisherLogo(key!, mediaType!, bytes!, actor.UserId, time.GetUtcNow().UtcDateTime), ctx.RequestAborted);
            audit.Record(new AuditEvent(AuditActions.PublisherLogoReplaced, actor, Details: Details(publisher.Name, key!)));
            return Results.NoContent();
        }).WithMetadata(new RequestSizeLimitAttribute(RequestLimit));

        api.MapDelete(Route, async (string? key, HttpContext ctx, IPublisherLogoRepository logos, IDiscoveryRepository discovery,
            IAuditLog audit) =>
        {
            if (KeyError(ctx, key) is { } invalid)
                return invalid;
            // The override outlives its publisher leaving the index (B5), so removal doesn't need it there.
            var label = (await discovery.GetGroupAsync(DiscoveryKind.Publishers, key!, ctx.RequestAborted))?.Name;
            if (!await logos.DeleteAsync(key!, ctx.RequestAborted))
                return Problems.NotFound(ctx);
            audit.Record(new AuditEvent(AuditActions.PublisherLogoRemoved, AuditActor.From(ctx.User), Details: Details(label, key!)));
            return Results.NoContent();
        });

        return api;
    }

    private static IResult? KeyError(HttpContext ctx, string? key) => DiscoveryKey.IsValid(key)
        ? null
        : Problems.Validation(ctx, new Dictionary<string, string[]> { ["key"] = ["Must be 64 lowercase hexadecimal characters."] });

    private static string Details(string? label, string key) => label is null ? $"key {key}" : $"{label} (key {key})";

    private static IResult TooLarge(HttpContext ctx) =>
        Problems.Create(ctx, StatusCodes.Status413PayloadTooLarge, "PayloadTooLarge", "The upload is larger than 576 KiB.");

    /// <summary>
    /// Buffers at most <see cref="RequestLimit"/> bytes of the body, then reads the form from that buffer. Kestrel also
    /// enforces the size-limit metadata; this covers hosts that don't (TestServer) and chunked bodies alike.
    /// </summary>
    private static async Task<(IFormCollection? Form, IResult? Problem)> ReadFormAsync(HttpContext ctx)
    {
        if (ctx.Request.ContentLength > RequestLimit)
            return (null, TooLarge(ctx));
        if (!ctx.Request.HasFormContentType)
            return (null, Problems.Validation(ctx, new Dictionary<string, string[]> { ["file"] = ["Send the logo as multipart/form-data."] }));

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

        buffer.Position = 0;
        ctx.Request.Body = buffer;
        try
        {
            return (await ctx.Request.ReadFormAsync(ctx.RequestAborted), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return (null, Problems.Validation(ctx, new Dictionary<string, string[]> { ["file"] = ["The form couldn't be read."] }));
        }
    }

    /// <summary>Exactly one PNG or JPEG in <c>file</c>, within the byte and pixel limits; else an error message.</summary>
    private static async Task<(byte[]? Bytes, string? MediaType, string? Error)> ReadLogoAsync(IFormCollection form, CancellationToken cancellationToken)
    {
        var files = form.Files.GetFiles("file");
        if (form.Files.Count != 1 || files.Count != 1)
            return (null, null, "Send exactly one image in the file field.");
        var file = files[0];
        if (file.Length == 0)
            return (null, null, "Choose an image file.");
        if (file.Length > MaxBytes)
            return (null, null, "The logo must be at most 512 KiB.");

        using var copy = new MemoryStream((int)file.Length);
        await file.CopyToAsync(copy, cancellationToken);
        var bytes = copy.ToArray();
        if (ImageFormats.Read(bytes) is not { } image)
            return (null, null, "The logo must be a PNG or JPEG image.");
        if (image.Width > MaxDimension || image.Height > MaxDimension)
            return (null, null, $"The logo must be at most {MaxDimension} pixels wide and high.");
        return (bytes, image.Format == ImageFormat.Png ? "image/png" : "image/jpeg", null);
    }
}
