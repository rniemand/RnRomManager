using Microsoft.Extensions.Logging;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Library;

namespace RnRomManager.Core.Storage;

public sealed partial class StorageMigrationJob
{
    /// <summary>
    /// ROM Zip Config B8 (into the ZIP): each file beside the ZIP is opened through the checked path with its snapshot
    /// and streamed in as a <c>roms/</c> entry; the writer verifies every entry's size and SHA-1 against <c>info.json</c>
    /// before it publishes, so a mismatch leaves ZIP and files untouched. After publish and mirror, each file is deleted
    /// if unchanged. A failed mirror deletes nothing (the DB rows still name the files; the resume re-mirrors).
    /// </summary>
    private async Task<StorageMigrationJobOutcome> EmbedAsync(GameRow row, InfoJsonGame game, LibraryZip existing)
    {
        var ct = CancellationToken.None;
        var library = paths.Value.Roms;
        var folder = Path.GetDirectoryName(existing.Path)!;
        var needed = game.Roms.Sum(r => r.Metadata.SizeBytes) + new FileInfo(existing.Path).Length + SpaceMarginBytes;
        if (freeSpace.AvailableBytes(existing.Path) is { } free && free < needed)
            return new("InsufficientSpace", $"{needed} bytes needed at {existing.Path}, {free} free");

        var sources = new List<(InfoJsonRom Rom, string Path, ExternalFileSnapshot Snapshot)>();
        foreach (var rom in game.Roms)
        {
            var check = ExternalRomPath.ClassifyFile(library, Path.Combine(folder, rom.ExternalFileName!));
            if (check.Status != ExternalPathStatus.Found)
                return new("IoError", $"{rom.ExternalFileName}: {check.Details}");
            sources.Add((rom, check.Path, check.Snapshot!));
        }

        var embedded = game with { Roms = [.. game.Roms.Select(r => r with { ExternalFileName = null })] };
        var entries = sources.ToDictionary(s => LibraryZip.RomEntry(s.Rom.FileName), s => (Func<Stream>)(() =>
        {
            var opened = ExternalRomPath.OpenRead(library, s.Path, FileShare.Read, s.Snapshot);
            return opened.Status == ExternalPathStatus.Found ? opened.Stream! : throw new IOException($"{s.Path}: {opened.Details}");
        }));
        ExternalFileSnapshot published;
        try
        {
            published = await Writer(embedded.System).WriteCheckedAsync(library, existing.Path, existing,
                new LibraryZipChanges(embedded, entries), ct, WriterHooks).ConfigureAwait(false);
        }
        catch (LibraryZipVerifyException ex)
        {
            return new("VerifyFailed", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new("IoError", ex.Message);
        }

        try
        {
            await mirror.ReplaceAsync(new MirrorEntry(embedded, row.ZipPath, published.Length, published.LastWriteTimeUtc), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            return new("DbWriteFailed", ex.Message);
        }
        foreach (var (_, path, snapshot) in sources)
        {
            if (ExternalRomPath.ClassifyFile(library, path).Snapshot == snapshot)
                File.Delete(path);
            else
                logger.LogError("{Path} changed while the storage job moved it into its ZIP; it was kept", path);
        }
        return new(Roms: game.Roms.Count, Bytes: game.Roms.Sum(r => r.Metadata.SizeBytes), Action: AuditActions.RomsEmbedded);
    }

    /// <summary>
    /// B8: a candidate whose ZIP is already in its system's layout. Its DB rows still showing the other layout (a crash
    /// between publish and mirror) are re-mirrored, so the game leaves the query; otherwise it is skipped.
    /// </summary>
    private async Task<StorageMigrationJobOutcome> RemirrorIfStaleAsync(GameRow row, InfoJsonGame game, string zip)
    {
        var rows = await roms.ListByGameAsync(row.Id, CancellationToken.None).ConfigureAwait(false);
        var dbEmbedded = rows.All(r => r.ExternalFileName is null);
        if (dbEmbedded == game.Roms.All(r => r.ExternalFileName is null))
            return new(Skipped: true);
        var file = new FileInfo(zip);
        try
        {
            await mirror.ReplaceAsync(new MirrorEntry(game, row.ZipPath, file.Length, file.LastWriteTimeUtc), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            return new("DbWriteFailed", ex.Message);
        }
        return new(Roms: game.Roms.Count);
    }
}
