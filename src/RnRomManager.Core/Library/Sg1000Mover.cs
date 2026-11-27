using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Media;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using static RnRomManager.Core.Library.LibraryEditResult;

namespace RnRomManager.Core.Library;

/// <summary>
/// SG-1000 S11: moves one Master System game whose releases were all dropped as <c>.sg</c> to <c>sg1000</c>, renaming its
/// releases to <c>.sg</c>. Takes no job lock: only <c>Sg1000MoveJob</c>, which holds it, calls it.
/// </summary>
internal sealed class Sg1000Mover(
    LibraryEditSupport libraryEditSupport,
    ReleaseEditor releaseEditor,
    ILibraryMirror mirror,
    InfoJsonReader reader,
    ISystemRegistry systems,
    IOptions<PathsOptions> paths,
    IAuditLog audit,
    TimeProvider time,
    ILogger<LibraryEditor> logger)
{
    internal const string FromSystemId = "sms";
    internal const string ToSystemId = "sg1000";
    internal const string Extension = ".sg";

    internal async Task<LibraryEditResult> MoveAsync(GameRow row, AuditActor actor)
    {
        var roms = paths.Value.Roms;
        // A database-only restore can leave the row pointing at a ZIP an earlier run already moved.
        if (!File.Exists(SafePath.Resolve(roms, row.ZipPath)))
            return new(NotSg1000, $"{row.ZipPath} is missing");
        var target = libraryEditSupport.OpenForEdit(row);
        if (target.Code is not null)
            return new(target.Code, target.Details);

        using var existing = target.Zip!;
        var current = target.Game!;
        if (current.GameId != row.Id || current.System != FromSystemId || current.Roms.Count == 0
            || current.Roms.Any(r => !r.OriginalFileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                || r.Identification.Method == IdentificationMethod.Dat))
            return new(NotSg1000, $"{row.ZipPath} isn't a Master System game of SG-1000 dumps");

        var entries = new Dictionary<string, Func<Stream>>();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var taken = current.Roms.Where(IsSg).Select(r => r.FileName).ToList();
        var renames = new List<(string From, string To)>();
        var moved = new List<InfoJsonRom>();
        foreach (var rom in current.Roms)
        {
            if (IsSg(rom))
            {
                moved.Add(rom);
                continue;
            }
            var name = LibraryEditSupport.FreeName(Path.GetFileNameWithoutExtension(rom.FileName) + Extension, taken);
            taken.Add(name);
            renames.Add((rom.FileName, name));
            entries[LibraryZip.RomEntry(name)] = () => existing.OpenEntry(LibraryZip.RomEntry(rom.FileName), rom.Metadata.SizeBytes);
            removed.Add(LibraryZip.RomEntry(rom.FileName));
            LibraryEditSupport.CarryNote(existing, rom.FileName, name, entries, removed);
            moved.Add(rom with { FileName = name });
        }

        SavesChange? saves = null;
        if (renames.Count > 0)
        {
            var read = GameSaves.Read(existing);
            if (read.State == SavesState.Unreadable)
                return new(SavesUnreadable, SavesUnreadableTitle);
            if (read.Index?.Saves.Any(s => renames.Any(r => GameSaves.Names(s, r.From))) == true)
            {
                var (savesError, change) = LibraryEditSupport.CarrySaves(existing, null, LibraryEditSupport.Renames([.. renames]));
                if (savesError is not null)
                    return savesError;
                saves = change;
            }
        }

        var images = current.Images.ToList();
        if (images.FirstOrDefault(i => i.IsDefault) is { } placeholder)
        {
            var art = MediaService.Default(systems.ById(ToSystemId)!);
            images[images.IndexOf(placeholder)] = art.ToInfoJson(time.GetUtcNow(), isCover: placeholder.IsCover) with { FileName = placeholder.FileName };
            entries[LibraryZip.ImageEntry(placeholder.FileName)] = () => new MemoryStream(art.Bytes);
        }

        var game = current with { System = ToSystemId, Roms = moved, Images = images, Metadata = libraryEditSupport.Touched(current) };
        string newPath;
        try
        {
            newPath = Leftover(row.Id, current.Title) ?? libraryEditSupport.NewZipPath(ToSystemId, current.Title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(IoError, ex.Message);
        }

        if (await libraryEditSupport.WriteZipAsync(existing, newPath, game, entries, removed, saves).ConfigureAwait(false) is { } failed)
            return failed;
        var result = await libraryEditSupport.MirrorAsync(() => mirror.ReplaceAsync(libraryEditSupport.Mirror(newPath, game), CancellationToken.None), newPath)
            .ConfigureAwait(false);
        if (result.Code is not null)
        {
            // The DB still says sms at the old path, which is untouched, so the next start retries cleanly.
            await releaseEditor.UndoTargetWriteAsync(newPath, null).ConfigureAwait(false);
            return result;
        }
        existing.Dispose(); // Windows can't delete an open file.
        libraryEditSupport.DeleteOldZip(row.ZipPath, row.Id);
        audit.Record(new AuditEvent(AuditActions.GameUpdated, actor, ToSystemId, row.Id, AuditPaths.Library(row.ZipPath),
            AuditPaths.Library(newPath), "Moved from Master System to SG-1000"));
        logger.LogInformation("Game {GameId} moved to SG-1000 ({ZipPath})", row.Id, newPath);
        return result;
    }

    private static bool IsSg(InfoJsonRom rom) => rom.FileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>A ZIP an interrupted earlier move wrote for this game in the folder <see cref="LibraryEditSupport.NewZipPath"/> would use.</summary>
    private string? Leftover(Guid gameId, string title)
    {
        var bucket = Bucket.For(FileNameSanitizer.Sanitize(title) + ".zip");
        var folder = Path.Combine(paths.Value.Roms, ToSystemId, bucket);
        if (!Directory.Exists(folder))
            return null;
        foreach (var file in Directory.GetFiles(folder, "*.zip").Order(StringComparer.Ordinal))
        {
            try
            {
                using var zip = LibraryZip.Open(file, reader);
                if (zip.InfoJson?.Game?.GameId == gameId)
                    return $"{ToSystemId}/{bucket}/{Path.GetFileName(file)}";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Skipping {File} while looking for an earlier SG-1000 move", file);
            }
        }
        return null;
    }
}
