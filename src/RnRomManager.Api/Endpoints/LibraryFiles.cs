using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// Opens library ZIPs for streaming (spec §3.1): <c>FileShare.Read | FileShare.Delete</c>, so an atomic replace or a
/// delete still works mid-download. A missing ZIP or entry is a <c>404</c> (the UI reloads the game, §11).
/// </summary>
public sealed class LibraryFiles(IOptions<PathsOptions> paths, InfoJsonReader reader, ILogger<LibraryFiles> logger)
{
    public IResult? OpenZip(HttpContext ctx, GameRow game, out FileStream? stream)
    {
        stream = null;
        try
        {
            stream = new FileStream(SafePath.Resolve(paths.Value.Roms, game.ZipPath), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            return null;
        }
        catch (Exception ex)
        {
            return Fail(ctx, game, ex);
        }
    }

    /// <summary>The entry stream, capped at <paramref name="maxBytes"/>; the ZIP is disposed with the response.</summary>
    public IResult? OpenEntry(HttpContext ctx, GameRow game, string entry, long maxBytes, out Stream? stream)
    {
        stream = null;
        LibraryZip? zip = null;
        try
        {
            zip = LibraryZip.Open(SafePath.Resolve(paths.Value.Roms, game.ZipPath), reader);
            stream = zip.OpenEntry(entry, maxBytes);
            ctx.Response.RegisterForDispose(zip);
            return null;
        }
        catch (Exception ex)
        {
            zip?.Dispose();
            return Fail(ctx, game, ex);
        }
    }

    /// <summary>
    /// GP3: the whole entry, buffered up to <paramref name="maxBytes"/> before responding, so a failure is never a
    /// <c>200</c> cut off mid-body (the central directory's length isn't trusted).
    /// </summary>
    public IResult? ReadEntry(HttpContext ctx, GameRow game, string entry, long maxBytes, out byte[]? bytes)
    {
        bytes = null;
        try
        {
            using var zip = LibraryZip.Open(SafePath.Resolve(paths.Value.Roms, game.ZipPath), reader);
            using var stream = zip.OpenEntry(entry, maxBytes);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
            return null;
        }
        catch (Exception ex)
        {
            return Fail(ctx, game, ex); // LimitExceededException and InvalidDataException → 500 IoError
        }
    }

    private IResult Fail(HttpContext ctx, GameRow game, Exception ex)
    {
        if (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogWarning("Library read for game {GameId} at {ZipPath}: {Message}", game.Id, game.ZipPath, ex.Message);
            return Problems.NotFound(ctx);
        }
        logger.LogError(ex, "Library read for game {GameId} at {ZipPath} failed", game.Id, game.ZipPath);
        return Problems.Create(ctx, StatusCodes.Status500InternalServerError, "IoError", "The game's ZIP can't be read");
    }
}
