using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Storage;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>/api/emulator/*</c>: files the browser play page needs besides the ROM (BIOS files, the optional Genesis core).
/// Any signed-in user; nothing is audited.
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

        // Genesis B4: one operator-supplied Genesis Plus GX file, served only when it matches the pinned manifest. The
        // verified in-memory copy is what's sent, so a file swapped after the check is never served; Range is ignored.
        api.MapGet("/emulator/genesis/{name}", async (string name, HttpContext ctx, IOptions<EmulatorOptions> options,
            GenesisCoreManifest manifest, ILogger<GenesisLog> logger) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var root = options.Value.GenesisCorePath;
            if (root.Length == 0 || manifest.Find(name) is not { } file)
                return Problems.NotFound(ctx);

            var (outcome, bytes) = await ReadGenesisFileAsync(root, file, logger, ctx.RequestAborted);
            return outcome switch
            {
                GenesisOutcome.Verified => Results.Bytes(bytes!, file.ContentType),
                GenesisOutcome.Mismatch => Problems.Create(ctx, StatusCodes.Status409Conflict, GenesisCoreMismatch,
                    "The Genesis core file doesn't match EmulatorJS 4.2.3"),
                GenesisOutcome.Unreadable => Problems.Create(ctx, StatusCodes.Status500InternalServerError, "IoError",
                    "The Genesis core file can't be read"),
                _ => Problems.NotFound(ctx),
            };
        });

        // Genesis B4: bodyless availability check of the whole set, run by the player before it fetches the ROM.
        api.MapMethods("/emulator/genesis", [HttpMethods.Head], async (HttpContext ctx, IOptions<EmulatorOptions> options,
            GenesisCoreManifest manifest, ILogger<GenesisLog> logger) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var root = options.Value.GenesisCorePath;
            if (root.Length == 0)
                return Results.StatusCode(StatusCodes.Status404NotFound);

            var outcomes = new List<GenesisOutcome>();
            foreach (var file in manifest.Files)
                outcomes.Add((await ReadGenesisFileAsync(root, file, logger, ctx.RequestAborted)).Outcome);
            return Results.StatusCode(
                outcomes.Contains(GenesisOutcome.Unreadable) ? StatusCodes.Status500InternalServerError
                : outcomes.Contains(GenesisOutcome.Mismatch) ? StatusCodes.Status409Conflict
                : outcomes.Contains(GenesisOutcome.Unavailable) ? StatusCodes.Status404NotFound
                : StatusCodes.Status204NoContent);
        });
        return api;
    }

    public const string GenesisCoreMismatch = "GenesisCoreMismatch";

    private enum GenesisOutcome { Verified, Unavailable, Mismatch, Unreadable }

    /// <summary>
    /// Genesis B4: opens <paramref name="file"/> under <paramref name="root"/> with the checked resolver (links and
    /// ambiguous case variants are refused), checks its length, reads exactly that many bytes and verifies the hash.
    /// </summary>
    private static async Task<(GenesisOutcome Outcome, byte[]? Bytes)> ReadGenesisFileAsync(string root, GenesisCoreFile file,
        ILogger logger, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return (GenesisOutcome.Unavailable, null); // a missing mount is "not set up", not a failure

        ExternalOpenResult opened;
        try
        {
            opened = ExternalRomPath.OpenRead(root, SafePath.Resolve(root, file.Name), FileShare.Read | FileShare.Delete);
        }
        catch (UnsafePathException)
        {
            return (GenesisOutcome.Unavailable, null);
        }

        switch (opened.Status)
        {
            case ExternalPathStatus.Found:
                break;
            case ExternalPathStatus.Unreadable:
                logger.LogError("Genesis core file {Name} in {Root} can't be read: {Details}", file.Name, root, opened.Details);
                return (GenesisOutcome.Unreadable, null);
            case ExternalPathStatus.Unsafe:
                logger.LogWarning("Genesis core file {Name} in {Root} is unsafe: {Details}", file.Name, root, opened.Details);
                return (GenesisOutcome.Unavailable, null);
            default:
                return (GenesisOutcome.Unavailable, null);
        }

        await using var stream = opened.Stream!;
        try
        {
            var length = stream.Length;
            if (length == 0)
                return (GenesisOutcome.Unavailable, null);
            if (length != file.Length)
            {
                logger.LogWarning("Genesis core file {Name} in {Root} is {Length} bytes, not the {Expected} of EmulatorJS 4.2.3; replace it",
                    file.Name, root, length, file.Length);
                return (GenesisOutcome.Mismatch, null);
            }
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256, StringComparison.Ordinal))
            {
                logger.LogWarning("Genesis core file {Name} in {Root} has a different SHA-256 than EmulatorJS 4.2.3; replace it",
                    file.Name, root);
                return (GenesisOutcome.Mismatch, null);
            }
            return (GenesisOutcome.Verified, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Genesis core file {Name} in {Root} can't be read", file.Name, root);
            return (GenesisOutcome.Unreadable, null);
        }
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

    /// <summary>Logger category for the Genesis core endpoints.</summary>
    public sealed class GenesisLog;
}
