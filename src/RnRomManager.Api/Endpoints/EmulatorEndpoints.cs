using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Storage;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// <c>/api/emulator/*</c>: files the browser play page needs besides the ROM (BIOS files, the optional operator-supplied cores).
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

        // B2: one operator-supplied core file, served only when it matches the pinned manifest. The verified in-memory
        // copy is what's sent, so a file swapped after the check is never served; Range is ignored.
        api.MapGet("/emulator/cores/{core}/{name}", (string core, string name, HttpContext ctx, IOptions<EmulatorOptions> options,
            OperatorCoreManifest manifest, ILogger<OperatorCoreLog> logger) => GetCoreFileAsync(core, name, ctx, options, manifest, logger));

        // B2: bodyless availability check of a core's whole set, run by the player before it fetches the ROM.
        api.MapMethods("/emulator/cores/{core}", [HttpMethods.Head], (string core, HttpContext ctx, IOptions<EmulatorOptions> options,
            OperatorCoreManifest manifest, ILogger<OperatorCoreLog> logger) => HeadCoreAsync(core, ctx, options, manifest, logger));

        // Genesis B4 routes, kept as aliases of the genesis_plus_gx ones.
        api.MapGet("/emulator/genesis/{name}", (string name, HttpContext ctx, IOptions<EmulatorOptions> options,
            OperatorCoreManifest manifest, ILogger<OperatorCoreLog> logger) =>
            GetCoreFileAsync(OperatorCoreManifest.GenesisPlusGx, name, ctx, options, manifest, logger));
        api.MapMethods("/emulator/genesis", [HttpMethods.Head], (HttpContext ctx, IOptions<EmulatorOptions> options,
            OperatorCoreManifest manifest, ILogger<OperatorCoreLog> logger) =>
            HeadCoreAsync(OperatorCoreManifest.GenesisPlusGx, ctx, options, manifest, logger));
        return api;
    }

    public const string GenesisCoreMismatch = "GenesisCoreMismatch";
    public const string PicodriveCoreMismatch = "PicodriveCoreMismatch";
    public const string Snes9xCoreMismatch = "Snes9xCoreMismatch";
    public const string OperaCoreMismatch = "OperaCoreMismatch";
    public const string ViceCoreMismatch = "ViceCoreMismatch";
    public const string Mupen64PlusCoreMismatch = "Mupen64PlusCoreMismatch";
    public const string Mame2003PlusCoreMismatch = "Mame2003PlusCoreMismatch";
    public const string FbneoCoreMismatch = "FbneoCoreMismatch";

    private enum CoreOutcome { Verified, Unavailable, Mismatch, Unreadable }

    private static async Task<IResult> GetCoreFileAsync(string coreId, string name, HttpContext ctx,
        IOptions<EmulatorOptions> options, OperatorCoreManifest manifest, ILogger logger)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (manifest.Find(coreId) is not { } core || core.PathOf(options.Value) is not { Length: > 0 } root
            || core.Find(name) is not { } file)
            return Problems.NotFound(ctx);

        var (outcome, bytes) = await ReadCoreFileAsync(core, root, file, logger, ctx.RequestAborted);
        return outcome switch
        {
            CoreOutcome.Verified => Results.Bytes(bytes!, file.ContentType),
            CoreOutcome.Mismatch => Problems.Create(ctx, StatusCodes.Status409Conflict, core.MismatchCode,
                $"The {core.DisplayName} core file doesn't match EmulatorJS 4.2.3"),
            CoreOutcome.Unreadable => Problems.Create(ctx, StatusCodes.Status500InternalServerError, "IoError",
                $"The {core.DisplayName} core file can't be read"),
            _ => Problems.NotFound(ctx),
        };
    }

    private static async Task<IResult> HeadCoreAsync(string coreId, HttpContext ctx, IOptions<EmulatorOptions> options,
        OperatorCoreManifest manifest, ILogger logger)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (manifest.Find(coreId) is not { } core || core.PathOf(options.Value) is not { Length: > 0 } root)
            return Results.StatusCode(StatusCodes.Status404NotFound);

        var outcomes = new List<CoreOutcome>();
        foreach (var file in core.Files)
            outcomes.Add((await ReadCoreFileAsync(core, root, file, logger, ctx.RequestAborted)).Outcome);
        return Results.StatusCode(
            outcomes.Contains(CoreOutcome.Unreadable) ? StatusCodes.Status500InternalServerError
            : outcomes.Contains(CoreOutcome.Mismatch) ? StatusCodes.Status409Conflict
            : outcomes.Contains(CoreOutcome.Unavailable) ? StatusCodes.Status404NotFound
            : StatusCodes.Status204NoContent);
    }

    /// <summary>
    /// B2: opens <paramref name="file"/> under <paramref name="root"/> with the checked resolver (links and ambiguous
    /// case variants are refused), checks its length, reads exactly that many bytes and verifies the hash.
    /// </summary>
    private static async Task<(CoreOutcome Outcome, byte[]? Bytes)> ReadCoreFileAsync(OperatorCore core, string root,
        OperatorCoreFile file, ILogger logger, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return (CoreOutcome.Unavailable, null); // a missing mount is "not set up", not a failure

        ExternalOpenResult opened;
        try
        {
            opened = ExternalRomPath.OpenRead(root, SafePath.Resolve(root, file.Name), FileShare.Read | FileShare.Delete);
        }
        catch (UnsafePathException)
        {
            return (CoreOutcome.Unavailable, null);
        }

        switch (opened.Status)
        {
            case ExternalPathStatus.Found:
                break;
            case ExternalPathStatus.Unreadable:
                logger.LogError("{Core} core file {Name} in {Root} can't be read: {Details}", core.DisplayName, file.Name, root, opened.Details);
                return (CoreOutcome.Unreadable, null);
            case ExternalPathStatus.Unsafe:
                logger.LogWarning("{Core} core file {Name} in {Root} is unsafe: {Details}", core.DisplayName, file.Name, root, opened.Details);
                return (CoreOutcome.Unavailable, null);
            default:
                return (CoreOutcome.Unavailable, null);
        }

        await using var stream = opened.Stream!;
        try
        {
            var length = stream.Length;
            if (length == 0)
                return (CoreOutcome.Unavailable, null);
            if (length != file.Length)
            {
                logger.LogWarning("{Core} core file {Name} in {Root} is {Length} bytes, not the {Expected} of EmulatorJS 4.2.3; replace it",
                    core.DisplayName, file.Name, root, length, file.Length);
                return (CoreOutcome.Mismatch, null);
            }
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256, StringComparison.Ordinal))
            {
                logger.LogWarning("{Core} core file {Name} in {Root} has a different SHA-256 than EmulatorJS 4.2.3; replace it",
                    core.DisplayName, file.Name, root);
                return (CoreOutcome.Mismatch, null);
            }
            return (CoreOutcome.Verified, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "{Core} core file {Name} in {Root} can't be read", core.DisplayName, file.Name, root);
            return (CoreOutcome.Unreadable, null);
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

    /// <summary>Logger category for the operator-supplied core endpoints.</summary>
    public sealed class OperatorCoreLog;
}
