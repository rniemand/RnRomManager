using System.IO.Compression;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;

namespace RnRomManager.Api.Endpoints;

/// <summary>
/// Opens library ZIPs for streaming (spec §3.1): <c>FileShare.Read | FileShare.Delete</c>, so an atomic replace or a
/// delete still works mid-download. A missing ZIP or entry is a <c>404</c> (the UI reloads the game, §11).
/// </summary>
public sealed class LibraryFiles(IOptions<PathsOptions> paths, InfoJsonReader reader, ISystemRegistry systems, ILogger<LibraryFiles> logger)
{
    public IResult? OpenZip(HttpContext ctx, GameRow game, out FileStream? stream)
    {
        stream = null;
        try
        {
            stream = OpenFile(game, out _);
            return null;
        }
        catch (Exception ex)
        {
            return Fail(ctx, game, ex);
        }
    }

    /// <summary>
    /// G4: an external ROM, the file <paramref name="externalFileName"/> in the ZIP's folder, opened through the
    /// checked resolver (no links, regular file only). A missing or unsafe file, a pointer that isn't a plain name,
    /// and a name another game also claims in that folder (<paramref name="claims"/>) are a <c>404</c>.
    /// </summary>
    public IResult? OpenExternal(HttpContext ctx, GameRow game, string externalFileName, IEnumerable<ExternalRomRow> claims, out FileStream? stream)
    {
        stream = null;
        var folder = game.ZipPath[..(game.ZipPath.LastIndexOf('/') + 1)];
        var conflict = claims.Any(c => c.GameId != game.Id
            && string.Equals(c.ExternalFileName, externalFileName, StringComparison.OrdinalIgnoreCase)
            && c.ZipPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && c.ZipPath.IndexOf('/', folder.Length) < 0);
        if (conflict || externalFileName.AsSpan().IndexOfAny('/', '\\') >= 0 || externalFileName is "." or "..")
        {
            logger.LogWarning("External ROM {ExternalFileName} of game {GameId} at {ZipPath} is {Reason}", externalFileName, game.Id,
                game.ZipPath, conflict ? "also claimed by another game" : "not a plain file name");
            return Problems.NotFound(ctx);
        }

        try
        {
            var opened = ExternalRomPath.OpenRead(paths.Value.Roms, SafePath.Resolve(paths.Value.Roms, folder + externalFileName),
                FileShare.Read | FileShare.Delete);
            if (opened.Status == ExternalPathStatus.Found)
            {
                stream = opened.Stream;
                return null;
            }
            if (opened.Status != ExternalPathStatus.Unreadable)
            {
                logger.LogWarning("External ROM of game {GameId} at {ZipPath}: {Details}", game.Id, game.ZipPath, opened.Details);
                return Problems.NotFound(ctx);
            }
            return Fail(ctx, game, new IOException(opened.Details));
        }
        catch (UnsafePathException ex)
        {
            logger.LogWarning("External ROM of game {GameId} at {ZipPath}: {Message}", game.Id, game.ZipPath, ex.Message);
            return Problems.NotFound(ctx);
        }
    }

    /// <summary>The entry stream, capped at <paramref name="maxBytes"/>; the ZIP is disposed with the response.</summary>
    public IResult? OpenEntry(HttpContext ctx, GameRow game, string entry, long maxBytes, out Stream? stream)
    {
        stream = null;
        LibraryZip? zip = null;
        try
        {
            zip = OpenLibraryZip(game);
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
            using var zip = OpenLibraryZip(game);
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

    /// <summary>
    /// Z2: the ZIP's file entries (folder entries left out), ordered by path ordinally, with the central directory's sizes.
    /// Read raw, without parsing <c>info.json</c>.
    /// </summary>
    public IResult? ListEntries(HttpContext ctx, GameRow game, out IReadOnlyList<ZipEntryInfo>? entries)
    {
        entries = null;
        try
        {
            using var archive = OpenArchive(game);
            entries = archive.Entries.Where(e => !e.FullName.EndsWith('/'))
                .Select(e => new ZipEntryInfo(e.FullName, e.Length, e.CompressedLength, e.LastWriteTime.UtcDateTime))
                .OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
            return null;
        }
        catch (Exception ex)
        {
            return Fail(ctx, game, ex);
        }
    }

    /// <summary>
    /// Z3: the file entry named exactly <paramref name="path"/>, capped at its central-directory length; the ZIP is
    /// disposed with the response. The name is only looked up in the archive, never used as a file system path.
    /// </summary>
    public IResult? OpenRawEntry(HttpContext ctx, GameRow game, string path, out Stream? stream)
    {
        stream = null;
        ZipArchive? archive = null;
        try
        {
            archive = OpenArchive(game);
            if (path.EndsWith('/') || archive.GetEntry(path) is not { } entry)
            {
                archive.Dispose();
                return Problems.NotFound(ctx);
            }
            stream = new LimitStream(entry.Open(), entry.Length);
            ctx.Response.RegisterForDispose(archive);
            return null;
        }
        catch (Exception ex)
        {
            archive?.Dispose();
            return Fail(ctx, game, ex);
        }
    }

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
        var file = OpenFile(game, out _);
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

    private LibraryZip OpenLibraryZip(GameRow game)
    {
        var file = OpenFile(game, out var path);
        return LibraryZip.Open(file, path, reader, ExternalRomPath.SnapshotOf(file));
    }

    /// <summary>
    /// The one way a metadata ZIP is opened for reading. G4: when the game's system or the system folder the ZIP
    /// sits in stores ROMs externally, the ZIP goes through the checked open (no links, regular file only); an
    /// unsafe path reads as missing (<c>404</c>).
    /// </summary>
    private FileStream OpenFile(GameRow game, out string path)
    {
        path = SafePath.Resolve(paths.Value.Roms, game.ZipPath);
        var folder = game.ZipPath.Split('/')[0];
        var isExternal = systems.ById(game.SystemId) is { EmbedRomsInZip: false }
            || systems.All.Any(h => !h.EmbedRomsInZip && string.Equals(h.Id, folder, StringComparison.OrdinalIgnoreCase));
        if (!isExternal)
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        var opened = ExternalRomPath.OpenRead(paths.Value.Roms, path, FileShare.Read | FileShare.Delete);
        return opened.Status switch
        {
            ExternalPathStatus.Found => opened.Stream!,
            ExternalPathStatus.Unreadable => throw new IOException(opened.Details),
            _ => throw new FileNotFoundException(opened.Details, path),
        };
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

/// <summary>One file entry of a library ZIP (Z2): sizes from the central directory, the DOS time read as UTC.</summary>
public sealed record ZipEntryInfo(string Path, long SizeBytes, long CompressedSizeBytes, DateTime ModifiedUtc);
