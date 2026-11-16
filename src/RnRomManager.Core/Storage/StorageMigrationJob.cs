using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Storage;

/// <summary>
/// S4–S6: moves every legacy game's embedded ROMs (a <see cref="ISystemHandler.LegacyEmbeddedRoms"/> system's ZIP
/// with all ROMs inside) next to its ZIP. Per game: each ROM entry is streamed to <c>cache/work/storage-migration/</c>
/// while hashed and checked against <c>info.json</c>, moved next to the ZIP under its external name, then the ZIP is
/// rewritten without <c>roms/</c> entries (notes, images and other entries carried) and mirrored. A failure before
/// the ZIP is published deletes the files this game created and leaves the ZIP untouched. One job item per game;
/// cancel and shutdown stop between games.
/// </summary>
public sealed class StorageMigrationJob(IGameRepository games, IRomRepository roms, ILibraryMirror mirror, InfoJsonReader reader,
    ISystemRegistry systems, IJobItemRepository items, IOptions<PathsOptions> paths, IAuditLog audit, TimeProvider time,
    ILogger<StorageMigrationJob> logger) : IJob
{
    public const string WorkFolder = "storage-migration";
    public const string Migrated = "Migrated";
    public const string Error = "Error";

    public JobType Type => JobType.StorageMigration;
    public JobMode? Mode => null;

    /// <summary>Test seams: the writer's hooks for the ZIP rewrite.</summary>
    public LibraryZipWriterHooks? WriterHooks { get; set; }

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var legacySystems = systems.All.Where(h => h.LegacyEmbeddedRoms).Select(h => h.Id).ToList();
        var candidates = await games.ListLegacyEmbeddedAsync(legacySystems, cancellationToken).ConfigureAwait(false);
        context.Progress.SetTotal(candidates.Count);
        int migrated = 0, error = 0;

        foreach (var row in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            context.Progress.SetCurrentItem(row.ZipPath);
            Outcome outcome;
            try
            {
                // Not cancellable once a game starts: its files and ZIP are never left half-moved.
                outcome = await MigrateAsync(row).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Storage migration of {ZipPath} failed", row.ZipPath);
                outcome = new("Unexpected", ex.Message);
            }

            if (outcome.Skipped)
            {
                logger.LogInformation("Storage migration: {ZipPath} no longer holds its ROMs; skipped", row.ZipPath);
            }
            else
            {
                if (outcome.Code is null)
                {
                    migrated++;
                    audit.Record(new AuditEvent(AuditActions.RomsExternalized, context.Actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                        Details: $"{outcome.Roms} ROM(s), {outcome.Bytes} bytes"));
                    logger.LogInformation("Storage migration: {ZipPath} now keeps {Roms} ROM(s) next to it", row.ZipPath, outcome.Roms);
                }
                else
                {
                    error++;
                    logger.LogError("Storage migration of {ZipPath} failed: {Code} {Details}", row.ZipPath, outcome.Code, outcome.Details);
                }
                await items.InsertAsync(new JobItem
                {
                    JobId = context.JobId,
                    FileId = row.Id,
                    GameId = row.Id,
                    SystemId = row.SystemId,
                    SourcePath = AuditPaths.Library(row.ZipPath),
                    Outcome = outcome.Code is null ? Migrated : Error,
                    Reason = outcome.Code,
                    Details = outcome.Details,
                    CreatedAt = time.GetUtcNow().UtcDateTime,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            context.Progress.ItemDone();
        }
        return new { migrated, error };
    }

    private sealed record Outcome(string? Code = null, string? Details = null, int Roms = 0, long Bytes = 0, bool Skipped = false);

    private async Task<Outcome> MigrateAsync(GameRow row)
    {
        var ct = CancellationToken.None;
        var library = paths.Value.Roms;
        var cache = paths.Value.Cache;
        var target = LibraryTarget.OpenChecked(library, row.ZipPath, reader, row.Id, row.SystemId);
        if (target.Code is not null)
            return new("ZipUnreadable", target.Details);
        using var existing = target.Zip!;
        var game = target.Game!;
        if (systems.ById(game.System) is not { } handler || !RomStorage.IsLegacy(handler, game))
            return new(Skipped: true);

        var folder = ExternalRomPath.ClassifyDirectory(library, Path.GetDirectoryName(existing.Path)!);
        if (folder.Status != ExternalPathStatus.Found)
            return new("IoError", folder.Details);
        var work = ExternalRomPath.CreateDirectory(cache, Path.Combine(cache, CrashRecovery.WorkFolder, WorkFolder, Guid.NewGuid().ToString("N")));
        if (work.Status != ExternalPathStatus.Found)
            return new("IoError", work.Details);

        var claims = await roms.ListExternalAsync(ct).ConfigureAwait(false);
        var names = ExternalNameReservations.ForFolder(folder.Path, row.ZipPath[..row.ZipPath.LastIndexOf('/')], claims);
        var zipBase = Path.GetFileNameWithoutExtension(row.ZipPath);
        var placed = new List<(string Path, ExternalFileSnapshot Snapshot)>();
        var migratedRoms = new List<InfoJsonRom>();
        try
        {
            foreach (var rom in game.Roms)
            {
                var tmp = Path.Combine(work.Path, Guid.NewGuid().ToString("N") + ".tmp");
                if (await CopyOutAsync(existing, rom, tmp).ConfigureAwait(false) is { } copyError)
                    return Fail(copyError);
                var copied = ExternalRomPath.ClassifyFile(cache, tmp);
                if (copied.Status != ExternalPathStatus.Found)
                    return Fail(new("IoError", copied.Details));

                var name = ExternalRomName.Allocate(zipBase, rom.FileName, names);
                var moved = ExternalRomMove.Move(cache, tmp, Path.Combine(folder.Path, name), copied.Snapshot!, logger, rom.Metadata.Sha1);
                if (moved.Status != ExternalMoveStatus.Moved)
                    return Fail(new("IoError", moved.Details));
                placed.Add((moved.Path!, moved.Snapshot!));
                migratedRoms.Add(rom with { ExternalFileName = name });
            }

            var migrated = game with { Roms = migratedRoms };
            ExternalFileSnapshot published;
            try
            {
                var touched = placed.ToDictionary(p => Path.GetFileName(p.Path), p => p.Snapshot, StringComparer.OrdinalIgnoreCase);
                published = await new LibraryZipWriter(reader).WriteCheckedAsync(library, existing.Path, existing,
                    new LibraryZipChanges(migrated, new Dictionary<string, Func<Stream>>(), ExternalFiles: touched), ct, WriterHooks).ConfigureAwait(false);
            }
            catch (LibraryZipVerifyException ex)
            {
                return Fail(new("VerifyFailed", ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail(new("IoError", ex.Message));
            }

            // The ZIP now names the files, so they stay even if the mirror fails (the next sync repairs the rows).
            placed.Clear();
            try
            {
                await mirror.ReplaceAsync(new MirrorEntry(migrated, row.ZipPath, published.Length, published.LastWriteTimeUtc), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                return new("DbWriteFailed", ex.Message);
            }
            return new(Roms: game.Roms.Count, Bytes: game.Roms.Sum(r => r.Metadata.SizeBytes));
        }
        finally
        {
            DeleteQuietly(work.Path);
        }

        Outcome Fail(Outcome failure)
        {
            // Nothing was published: only files this run created and still holds are removed.
            foreach (var (path, snapshot) in placed)
            {
                if (ExternalRomPath.ClassifyFile(library, path).Snapshot == snapshot)
                    File.Delete(path);
                else
                    logger.LogError("{Path} changed after the storage migration placed it; it was kept", path);
            }
            return failure;
        }
    }

    /// <summary>Streams one ROM entry to <paramref name="tmp"/>, hashing it, and checks size and SHA-1 against <c>info.json</c>.</summary>
    private static async Task<Outcome?> CopyOutAsync(LibraryZip zip, InfoJsonRom rom, string tmp)
    {
        var entry = LibraryZip.RomEntry(rom.FileName);
        if (zip.EntryLength(entry) is not { } length || length != rom.Metadata.SizeBytes)
            return new("VerifyFailed", $"{entry}: missing or not {rom.Metadata.SizeBytes} bytes");
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        long written = 0;
        try
        {
            await using var source = zip.OpenEntry(entry, rom.Metadata.SizeBytes);
            await using var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                sha1.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                written += read;
            }
            output.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or LimitExceededException)
        {
            return new("ZipUnreadable", $"{entry}: {ex.Message}");
        }
        var actual = Convert.ToHexStringLower(sha1.GetHashAndReset());
        return written == rom.Metadata.SizeBytes && actual == rom.Metadata.Sha1
            ? null
            : new("VerifyFailed", $"{entry}: SHA1 {actual} ({written} bytes), info.json {rom.Metadata.Sha1}");
    }

    private void DeleteQuietly(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't remove {Path}: {Message}; crash recovery cleans it at the next start", directory, ex.Message);
        }
    }
}
