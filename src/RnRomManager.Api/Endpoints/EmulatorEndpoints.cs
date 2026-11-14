using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Storage;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>/api/emulator/*</c>: files the browser play page needs besides the ROM. Any signed-in user; nothing is audited.
/// </summary>
public static partial class EmulatorEndpoints
{
    public static RouteGroupBuilder MapEmulatorEndpoints(this RouteGroupBuilder api)
    {
        // B3: a BIOS file the operator copied into Paths:Bios (e.g. colecovision.rom). HEAD stays unmapped (the /api
        // catch-all answers 404); EmulatorJS falls back to GET.
        api.MapGet("/emulator/bios/{name}", (string name, HttpContext ctx, IOptions<PathsOptions> paths, ILogger<BiosLog> logger) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!IsBiosName(name))
                return Problems.NotFound(ctx);

            var root = paths.Value.Bios;
            ExternalOpenResult opened;
            try
            {
                opened = ExternalRomPath.OpenRead(root, SafePath.Resolve(root, name), FileShare.Read | FileShare.Delete);
            }
            catch (UnsafePathException)
            {
                return Problems.NotFound(ctx);
            }

            switch (opened.Status)
            {
                case ExternalPathStatus.Found:
                    var stream = opened.Stream!;
                    if (stream.Length == 0)
                    {
                        stream.Dispose();
                        return Problems.NotFound(ctx);
                    }
                    // The response owns and disposes the checked stream.
                    return Results.Stream(stream, "application/octet-stream", enableRangeProcessing: false);
                case ExternalPathStatus.Unreadable:
                    logger.LogError("BIOS file {Name} in {BiosRoot} can't be read: {Details}", name, root, opened.Details);
                    return Problems.Create(ctx, StatusCodes.Status500InternalServerError, "IoError", "The BIOS file can't be read");
                case ExternalPathStatus.Unsafe:
                    logger.LogWarning("BIOS file {Name} in {BiosRoot} is unsafe: {Details}", name, root, opened.Details);
                    return Problems.NotFound(ctx);
                default:
                    return Problems.NotFound(ctx);
            }
        });
        return api;
    }

    /// <summary>
    /// A whole plain file name: letters, digits, <c>_</c>, <c>.</c> and <c>-</c>, 1–64 characters, starting with a
    /// letter or digit and not ending in a dot (Windows would drop it and open another name).
    /// </summary>
    public static bool IsBiosName(string name) => BiosName().IsMatch(name) && !name.EndsWith('.');

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\z")]
    private static partial Regex BiosName();

    /// <summary>Logger category for the BIOS endpoint.</summary>
    public sealed class BiosLog;
}
