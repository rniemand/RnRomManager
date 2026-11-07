using System.IO.Compression;
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

    /// <summary>
    /// B4: the release's note. Missing → <c>404</c>; over 64 KiB → <c>409 NoteTooLarge</c>; an unreadable ZIP →
    /// <c>409 TargetZipUnreadable</c>. Only the entry is read, so a newer-schema ZIP still works.
    /// </summary>
    public IResult? ReadNote(HttpContext ctx, GameRow game, string romFileName, out byte[]? bytes)
    {
        bytes = null;
        try
        {
            using var archive = OpenArchive(game);
            if (archive.GetEntry(LibraryZip.NoteEntry(romFileName)) is not { } entry)
                return Problems.NotFound(ctx);
            using var stream = new LimitStream(entry.Open(), LibraryZip.MaxNoteBytes);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
            return null;
        }
        catch (LimitExceededException)
        {
            return Problems.Create(ctx, StatusCodes.Status409Conflict, NoteTooLarge, "The note is larger than 64 KiB.");
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning("Note read for game {GameId} at {ZipPath}: {Message}", game.Id, game.ZipPath, ex.Message);
            return Problems.Create(ctx, StatusCodes.Status409Conflict, LibraryTarget.TargetZipUnreadable, LibraryTarget.TargetZipUnreadable);
        }
        catch (Exception ex)
        {
            return Fail(ctx, game, ex);
        }
    }

    public const string NoteTooLarge = "NoteTooLarge";

    /// <summary>B3: the ZIP's <c>notes/</c> entry names; empty (logged) when the ZIP can't be opened.</summary>
    public IReadOnlySet<string> NoteEntries(GameRow game)
    {
        try
        {
            using var archive = OpenArchive(game);
            return archive.Entries.Select(e => e.FullName).Where(n => n.StartsWith(LibraryZip.NotesFolder, StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning("Notes of game {GameId} at {ZipPath} can't be listed: {Message}", game.Id, game.ZipPath, ex.Message);
            return new HashSet<string>();
        }
    }

    /// <summary>The raw archive, without parsing <c>info.json</c> (notes don't depend on its schema).</summary>
    private ZipArchive OpenArchive(GameRow game)
    {
        var file = new FileStream(SafePath.Resolve(paths.Value.Roms, game.ZipPath), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            return new ZipArchive(file, ZipArchiveMode.Read);
        }
        catch
        {
            file.Dispose();
            throw;
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
