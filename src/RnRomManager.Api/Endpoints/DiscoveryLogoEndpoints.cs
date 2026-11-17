using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using RnRomManager.Api.Auth;
using RnRomManager.Api.Http;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Discovery;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>/api/discovery/{kind}/logo?key=</c> (collections discovery B5, B13–B14): the shared logo of a publisher, genre or
/// system. Any signed-in user reads it; Admins replace or remove it, and every change is audited. Publishers and genres
/// are keyed by their group key, systems by their id (stored as <see cref="DiscoveryKey.ForSystem"/>). The bytes live in
/// <c>discovery_logos</c>.
/// </summary>
public static partial class DiscoveryLogoEndpoints
{
    /// <summary>Largest stored logo.</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>Largest request body: the logo plus room for the multipart framing.</summary>
    public const int RequestLimit = 576 * 1024;

    /// <summary>Largest width and height.</summary>
    public const int MaxDimension = 2048;

    private const string Route = "/discovery/{kind}/logo";

    public sealed record LogoUrlRequest(string? Url);

    /// <summary>The route name of each kind.</summary>
    private static readonly IReadOnlyDictionary<string, LogoKind> Kinds = new Dictionary<string, LogoKind>(StringComparer.Ordinal)
    {
        ["publishers"] = LogoKind.Publishers,
        ["genres"] = LogoKind.Genres,
        ["systems"] = LogoKind.Systems,
    };

    [GeneratedRegex("^[a-z0-9]{1,32}$")]
    private static partial Regex SystemIdShape();

    /// <summary>B6, B15: the logo's URL, versioned by its <c>updated_at</c> so a change shows at once. <paramref name="key"/> is the route key (a system's id).</summary>
    internal static string LogoUrl(LogoKind kind, string key, DateTime updatedAt) =>
        $"/api/discovery/{kind.ToString().ToLowerInvariant()}/logo?key={key}&v={updatedAt.Ticks}";

    public static RouteGroupBuilder MapDiscoveryLogoEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet(Route, async (string kind, string? key, HttpContext ctx, IDiscoveryLogoRepository logos) =>
        {
            if (!Kinds.TryGetValue(kind, out var parsed))
                return Problems.NotFound(ctx);
            if (KeyError(ctx, parsed, key) is { } invalid)
                return invalid;
            if (await logos.GetAsync(parsed, StoredKey(parsed, key!), ctx.RequestAborted) is not { } logo)
                return Problems.NotFound(ctx);
            ctx.Response.Headers.CacheControl = "private, no-cache";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(logo.Bytes, logo.MediaType);
        });

        api.MapPut(Route, async (string kind, string? key, HttpContext ctx, IDiscoveryLogoRepository logos, IDiscoveryRepository discovery,
            ISystemRegistry systems, IAuditLog audit, TimeProvider time) =>
        {
            if (!Kinds.TryGetValue(kind, out var parsed))
                return Problems.NotFound(ctx);
            if (KeyError(ctx, parsed, key) is { } invalid)
                return invalid;
            var (form, problem) = await ReadFormAsync(ctx);
            if (problem is not null)
                return problem;
            var (bytes, mediaType, error) = await ReadLogoAsync(form!, ctx.RequestAborted);
            if (error is not null)
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["file"] = [error] });
            if (await NameAsync(parsed, key!, discovery, systems, ctx.RequestAborted) is not { } name)
                return Problems.NotFound(ctx);

            var actor = AuditActor.From(ctx.User);
            await logos.UpsertAsync(new DiscoveryLogo(parsed, StoredKey(parsed, key!), mediaType!, bytes!, actor.UserId, time.GetUtcNow().UtcDateTime),
                ctx.RequestAborted);
            audit.Record(Event(parsed, replaced: true, actor, name, key!));
            return Results.NoContent();
        }).RequireAuthorization(AuthSetup.AdminOnly).WithMetadata(new RequestSizeLimitAttribute(RequestLimit));

        // B14: key, group, URL, download, then the upload's own checks; the downloader only reaches public addresses.
        api.MapPut(Route + "/from-url", async (string kind, string? key, LogoUrlRequest? request, HttpContext ctx, IDiscoveryLogoRepository logos,
            IDiscoveryRepository discovery, ISystemRegistry systems, ImageUrlDownloader downloader, IAuditLog audit, TimeProvider time) =>
        {
            if (!Kinds.TryGetValue(kind, out var parsed))
                return Problems.NotFound(ctx);
            if (KeyError(ctx, parsed, key) is { } invalid)
                return invalid;
            if (await NameAsync(parsed, key!, discovery, systems, ctx.RequestAborted) is not { } name)
                return Problems.NotFound(ctx);
            if (LibraryEndpoints.ParseImageUrl(request?.Url) is not { } url)
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["url"] = ["Enter an http or https image URL."] });
            var download = await downloader.DownloadAsync(url, ctx.RequestAborted);
            if (download.Bytes is not { } bytes)
                return Problems.Create(ctx, download.Status, download.Code!, download.Title!);
            var (mediaType, error) = CheckLogo(bytes);
            if (error is not null)
                return Problems.Validation(ctx, new Dictionary<string, string[]> { ["url"] = [error] });

            var actor = AuditActor.From(ctx.User);
            await logos.UpsertAsync(new DiscoveryLogo(parsed, StoredKey(parsed, key!), mediaType!, bytes, actor.UserId, time.GetUtcNow().UtcDateTime),
                ctx.RequestAborted);
            audit.Record(Event(parsed, replaced: true, actor, name, key!, $" from {request!.Url}"));
            return Results.NoContent();
        }).RequireAuthorization(AuthSetup.AdminOnly);

        api.MapDelete(Route, async (string kind, string? key, HttpContext ctx, IDiscoveryLogoRepository logos, IDiscoveryRepository discovery,
            ISystemRegistry systems, IAuditLog audit) =>
        {
            if (!Kinds.TryGetValue(kind, out var parsed))
                return Problems.NotFound(ctx);
            if (KeyError(ctx, parsed, key) is { } invalid)
                return invalid;
            // The logo outlives its group leaving the index (B5), so removal doesn't need it there.
            var name = await NameAsync(parsed, key!, discovery, systems, ctx.RequestAborted);
            if (!await logos.DeleteAsync(parsed, StoredKey(parsed, key!), ctx.RequestAborted))
                return Problems.NotFound(ctx);
            audit.Record(Event(parsed, replaced: false, AuditActor.From(ctx.User), name, key!));
            return Results.NoContent();
        }).RequireAuthorization(AuthSetup.AdminOnly);

        return api;
    }

    private static IResult? KeyError(HttpContext ctx, LogoKind kind, string? key) => kind == LogoKind.Systems
        ? key is not null && SystemIdShape().IsMatch(key)
            ? null
            : Problems.Validation(ctx, new Dictionary<string, string[]> { ["key"] = ["Must be 1 to 32 lowercase letters or digits."] })
        : DiscoveryKey.IsValid(key)
            ? null
            : Problems.Validation(ctx, new Dictionary<string, string[]> { ["key"] = ["Must be 64 lowercase hexadecimal characters."] });

    private static string StoredKey(LogoKind kind, string key) => kind == LogoKind.Systems ? DiscoveryKey.ForSystem(key) : key;

    /// <summary>The current group's name or the registered system's display name; null when there is none.</summary>
    private static async Task<string?> NameAsync(LogoKind kind, string key, IDiscoveryRepository discovery, ISystemRegistry systems,
        CancellationToken cancellationToken) => kind switch
    {
        LogoKind.Systems => systems.ById(key)?.DisplayName,
        LogoKind.Genres => (await discovery.GetGroupAsync(DiscoveryKind.Genres, key, cancellationToken))?.Name,
        _ => (await discovery.GetGroupAsync(DiscoveryKind.Publishers, key, cancellationToken))?.Name,
    };

    /// <summary>B14: the audit event; system events carry the system id so the Auditing System filter finds them.</summary>
    private static AuditEvent Event(LogoKind kind, bool replaced, AuditActor actor, string? name, string key, string? suffix = null)
    {
        var (action, details) = kind switch
        {
            LogoKind.Systems => (replaced ? AuditActions.SystemLogoReplaced : AuditActions.SystemLogoRemoved,
                name is null ? $"system {key}" : $"{name} (system {key})"),
            LogoKind.Genres => (replaced ? AuditActions.GenreLogoReplaced : AuditActions.GenreLogoRemoved,
                name is null ? $"key {key}" : $"{name} (key {key})"),
            _ => (replaced ? AuditActions.PublisherLogoReplaced : AuditActions.PublisherLogoRemoved,
                name is null ? $"key {key}" : $"{name} (key {key})"),
        };
        return new AuditEvent(action, actor, SystemId: kind == LogoKind.Systems ? key : null, Details: details + suffix);
    }

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
        var (mediaType, error) = CheckLogo(bytes);
        return error is null ? (bytes, mediaType, null) : (null, null, error);
    }

    /// <summary>B14: the checks shared by the file and URL uploads: PNG or JPEG by content, within the byte and pixel limits.</summary>
    internal static (string? MediaType, string? Error) CheckLogo(byte[] bytes)
    {
        if (bytes.Length > MaxBytes)
            return (null, "The logo must be at most 512 KiB.");
        if (ImageFormats.Read(bytes) is not { } image)
            return (null, "The logo must be a PNG or JPEG image.");
        if (image.Width > MaxDimension || image.Height > MaxDimension)
            return (null, $"The logo must be at most {MaxDimension} pixels wide and high.");
        return (image.Format == ImageFormat.Png ? "image/png" : "image/jpeg", null);
    }
}
