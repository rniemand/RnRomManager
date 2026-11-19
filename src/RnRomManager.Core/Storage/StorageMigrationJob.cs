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
/// ROM Zip Config B7–B8: moves every game whose layout differs from its system's storage, in either direction. Into the
/// ZIP: see <c>StorageMigrationJob.Embed.cs</c>. Out of the ZIP, S4–S6: moves every legacy game's embedded ROMs (a <see cref="ISystemHandler.LegacyEmbeddedRoms"/> system's ZIP
/// with all ROMs inside) next to its ZIP. Per game: each ROM entry is streamed to <c>cache/work/storage-migration/</c>
/// while hashed and checked against <c>info.json</c>, moved next to the ZIP under its external name, then the ZIP is
/// rewritten without <c>roms/</c> entries (notes, images and other entries carried) and mirrored. A failure before
/// the ZIP is published deletes the files this game created and leaves the ZIP untouched. A leftover of a crash after
/// the move (an unclaimed file with the ROM's name, size and SHA-1) is reused. A game needs its ROMs + its ZIP + 64 MiB
/// free on the library volume and its largest ROM free on the cache volume. One job item per
/// game; cancel and shutdown stop between games.
/// </summary>
public sealed partial class StorageMigrationJob(IGameRepository games, IRomRepository roms, ILibraryMirror mirror, InfoJsonReader reader,
    ISystemRegistry systems, IJobItemRepository items, IOptions<PathsOptions> paths, IAuditLog audit, IFreeSpace freeSpace, TimeProvider time,
    IRomStorageSettings romStorage, ILogger<StorageMigrationJob> logger) : IJob
{
    /// <summary>S6: room kept free on the library volume on top of the game's ROMs and its ZIP.</summary>
    public const long SpaceMarginBytes = 64L * 1024 * 1024;

    /// <summary>Z4: a writer at the game's system ZIP compression level.</summary>
    private LibraryZipWriter Writer(string systemId) => new(reader, romStorage.Compression(systemId));

    public const string WorkFolder = "storage-migration";
    public const string Migrated = "Migrated";
    public const string Error = "Error";

    public JobType Type => JobType.StorageMigration;
    public JobMode? Mode => null;

    /// <summary>B7: the Admin's storage change this run applies first, under the job lock; null for a resume.</summary>
    public RomStorageChange? Change { get; set; }

    /// <summary>Test seams: the writer's hooks for the ZIP rewrite.</summary>
    public LibraryZipWriterHooks? WriterHooks { get; set; }

    /// <summary>B7: the ids of the <see cref="ISystemHandler.LegacyEmbeddedRoms"/> systems, split by their current storage.</summary>
    public static (IReadOnlyList<string> Embed, IReadOnlyList<string> External) MoveSystems(ISystemRegistry systems)
    {
        var movable = systems.All.Where(h => h.LegacyEmbeddedRoms).ToList();
        return ([.. movable.Where(h => h.EmbedRomsInZip).Select(h => h.Id)], [.. movable.Where(h => !h.EmbedRomsInZip).Select(h => h.Id)]);
    }

    /// <summary>B7: the games the job would move (candidates; each ZIP is re-opened to decide).</summary>
    public static Task<IReadOnlyList<GameRow>> ListMovesAsync(IGameRepository games, ISystemRegistry systems, CancellationToken cancellationToken)
    {
        var (embed, external) = MoveSystems(systems);
        return games.ListStorageMovesAsync(embed, external, cancellationToken);
    }

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        // B7: a failed save fails the job before anything moves; a change already in effect saves and audits nothing.
        if (Change is { } change && systems.ById(change.SystemId) is { RomStorageConfigurable: true } changed && changed.EmbedRomsInZip != change.Embed)
        {
            await romStorage.SetAsync(change.SystemId, change.Embed, cancellationToken).ConfigureAwait(false);
            audit.Record(new AuditEvent(AuditActions.RomStorageChanged, context.Actor, change.SystemId,
                Details: change.Embed ? "in ZIP" : "beside ZIP"));
            logger.LogInformation("ROM storage of {SystemId} set to {Storage}", change.SystemId, change.Embed ? "in ZIP" : "beside ZIP");
        }
        var candidates = await ListMovesAsync(games, systems, cancellationToken).ConfigureAwait(false);
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
                logger.LogInformation("Storage migration: {ZipPath} is already in its system's layout; skipped", row.ZipPath);
            }
            else
            {
                if (outcome.Code is null)
                {
                    migrated++;
                    if (outcome.Action is { } action)
                        audit.Record(new AuditEvent(action, context.Actor, row.SystemId, row.Id, AuditPaths.Library(row.ZipPath),
                            Details: $"{outcome.Roms} ROM(s), {outcome.Bytes} bytes"));
                    logger.LogInformation("Storage migration: {ZipPath}: {Roms} ROM(s) moved {Direction}", row.ZipPath, outcome.Roms,
                        outcome.Action == AuditActions.RomsEmbedded ? "into it" : outcome.Action is null ? "earlier; re-mirrored" : "next to it");
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
                    Details = outcome.Code is null && outcome.Action is null ? "already moved; database updated" : outcome.Details,
                    CreatedAt = time.GetUtcNow().UtcDateTime,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            context.Progress.ItemDone();
        }
        return new { migrated, error };
    }

    /// <param name="Action">The audit action of a move; null for a success that only re-mirrored (B8).</param>
    private sealed record Outcome(string? Code = null, string? Details = null, int Roms = 0, long Bytes = 0, bool Skipped = false, string? Action = null);

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
        if (systems.ById(game.System) is not { } handler)
            return new(Skipped: true);
        if (!RomStorage.IsPending(handler, game))
            return await RemirrorIfStaleAsync(row, game, existing.Path).ConfigureAwait(false);
        if (handler.EmbedRomsInZip)
            return await EmbedAsync(row, game, existing).ConfigureAwait(false);

        var folder = ExternalRomPath.ClassifyDirectory(library, Path.GetDirectoryName(existing.Path)!);
        if (folder.Status != ExternalPathStatus.Found)
            return new("IoError", folder.Details);
        if (SpaceProblem(game, existing.Path, cache) is { } space)
            return new("InsufficientSpace", space);
        var work = ExternalRomPath.CreateDirectory(cache, Path.Combine(cache, CrashRecovery.WorkFolder, WorkFolder, Guid.NewGuid().ToString("N")));
        if (work.Status != ExternalPathStatus.Found)
            return new("IoError", work.Details);

        var claims = await roms.ListExternalAsync(ct).ConfigureAwait(false);
        var folderPath = row.ZipPath[..row.ZipPath.LastIndexOf('/')];
        var names = ExternalNameReservations.ForFolder(folder.Path, folderPath, claims);
        var claimed = claims.Where(c => string.Equals(c.ZipPath[..c.ZipPath.LastIndexOf('/')], folderPath, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.ExternalFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var zipBase = Path.GetFileNameWithoutExtension(row.ZipPath);
        // Every file the new ZIP points at; only those this run created are removed on failure.
        var placed = new List<(string Path, ExternalFileSnapshot Snapshot)>();
        var created = new List<(string Path, ExternalFileSnapshot Snapshot)>();
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

                // The walk Allocate does, but a crash leftover (unclaimed, same size and SHA-1) is taken as it is.
                for (var n = 1; ; n++)
                {
                    var name = ExternalRomName.Build(zipBase, rom.FileName, n);
                    var staging = ExternalRomName.StagingName(name);
                    if (!names.IsTaken(name) && !names.IsTaken(staging))
                    {
                        names.Reserve(name);
                        names.Reserve(staging);
                        var moved = ExternalRomMove.Move(cache, tmp, Path.Combine(folder.Path, name), copied.Snapshot!, logger, rom.Metadata.Sha1);
                        if (moved.Status != ExternalMoveStatus.Moved)
                            return Fail(new("IoError", moved.Details));
                        placed.Add((moved.Path!, moved.Snapshot!));
                        created.Add((moved.Path!, moved.Snapshot!));
                        migratedRoms.Add(rom with { ExternalFileName = name });
                        break;
                    }
                    if (!claimed.Contains(name) && placed.All(p => !string.Equals(Path.GetFileName(p.Path), name, StringComparison.OrdinalIgnoreCase))
                        && await LeftoverAsync(Path.Combine(folder.Path, name), rom).ConfigureAwait(false) is { } leftover)
                    {
                        logger.LogInformation("Storage migration: reusing {Path}, left by an earlier run", leftover.Path);
                        File.Delete(tmp);
                        placed.Add((leftover.Path, leftover.Snapshot!));
                        migratedRoms.Add(rom with { ExternalFileName = name });
                        break;
                    }
                }
            }

            var migrated = game with { Roms = migratedRoms };
            ExternalFileSnapshot published;
            try
            {
                var touched = placed.ToDictionary(p => Path.GetFileName(p.Path), p => p.Snapshot, StringComparer.OrdinalIgnoreCase);
                published = await Writer(migrated.System).WriteCheckedAsync(library, existing.Path, existing,
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
            created.Clear();
            try
            {
                await mirror.ReplaceAsync(new MirrorEntry(migrated, row.ZipPath, published.Length, published.LastWriteTimeUtc), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                return new("DbWriteFailed", ex.Message);
            }
            return new(Roms: game.Roms.Count, Bytes: game.Roms.Sum(r => r.Metadata.SizeBytes), Action: AuditActions.RomsExternalized);
        }
        finally
        {
            DeleteQuietly(work.Path);
        }

        Outcome Fail(Outcome failure)
        {
            // Nothing was published: only files this run created and still holds are removed.
            foreach (var (path, snapshot) in created)
            {
                if (ExternalRomPath.ClassifyFile(library, path).Snapshot == snapshot)
                    File.Delete(path);
                else
                    logger.LogError("{Path} changed after the storage migration placed it; it was kept", path);
            }
            return failure;
        }
    }

    /// <returns>Why the game doesn't fit (S6), or null; an unreadable free space is logged and doesn't stop it.</returns>
    private string? SpaceProblem(InfoJsonGame game, string zip, string cache)
    {
        var needed = game.Roms.Sum(r => r.Metadata.SizeBytes) + new FileInfo(zip).Length + SpaceMarginBytes;
        // On a shared volume the cache check is implied by the library one (same free bytes, a smaller need).
        return Short(zip, needed) ?? Short(cache, game.Roms.Max(r => r.Metadata.SizeBytes));

        string? Short(string path, long bytes)
        {
            if (freeSpace.AvailableBytes(path) is not { } free)
            {
                logger.LogWarning("Storage migration: the free space at {Path} can't be read; going ahead", path);
                return null;
            }
            return free < bytes ? $"{bytes} bytes needed at {path}, {free} free" : null;
        }
    }

    /// <returns>The existing file at <paramref name="path"/> when it holds exactly <paramref name="rom"/>'s bytes; otherwise null.</returns>
    private async Task<ExternalPathCheck?> LeftoverAsync(string path, InfoJsonRom rom)
    {
        var check = ExternalRomPath.ClassifyFile(paths.Value.Roms, path);
        if (check.Status != ExternalPathStatus.Found || check.Snapshot!.Length != rom.Metadata.SizeBytes)
            return null;
        var opened = ExternalRomPath.OpenRead(paths.Value.Roms, check.Path, FileShare.Read, check.Snapshot);
        if (opened.Status != ExternalPathStatus.Found)
            return null;
        await using var stream = opened.Stream!;
        var sha1 = Convert.ToHexStringLower(await SHA1.HashDataAsync(stream).ConfigureAwait(false));
        return sha1 == rom.Metadata.Sha1 ? check : null;
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
