using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Library;

public enum DbSyncChange
{
    Inserted,

    /// <summary>Changed ZIP, or the same <c>gameId</c> found at another path (<c>zip_path</c> updated).</summary>
    Updated,

    Deleted,
}

/// <summary>Why a ZIP's DB rows were left as they are (§12.2).</summary>
public enum DbSyncSkip
{
    ZipUnreadable,
    InfoJsonMissing,
    InfoJsonInvalid,
    InfoJsonNewerSchema,
    DuplicateGameId,
    DuplicateSha1,
}

public sealed record DbSyncChangeItem(DbSyncChange Kind, Guid GameId, string ZipPath);

public sealed record DbSyncSkipItem(DbSyncSkip Reason, string ZipPath, string? Details);

public sealed record DbSyncResult(IReadOnlyList<DbSyncChangeItem> Changes, IReadOnlyList<DbSyncSkipItem> Skipped);

/// <summary>
/// The DB auto-repair shared by the ingest pre-pass (§7.1 step 4) and integrity (§12.2, D10): every <c>.zip</c> under a
/// registered system folder is compared with its <c>games</c> row (size + whole-second mtime); new or changed ZIPs are
/// re-read and written, rows whose ZIP is gone are deleted unless their <c>gameId</c> turned up at another path.
/// Unreadable ZIPs and duplicate <c>gameId</c>s keep their rows; a ZIP whose ROM SHA1 another game already holds is
/// skipped (<c>roms.sha1</c> is unique). All writes go in one transaction. File-side problems are reported, never fixed.
/// </summary>
public sealed class DbSync(
    IGameRepository games,
    IRomRepository roms,
    ILibraryMirror mirror,
    InfoJsonReader reader,
    ISystemRegistry systems,
    IOptions<PathsOptions> paths,
    ILogger<DbSync> logger)
{
    private sealed record Found(string ZipPath, Guid Id, MirrorEntry? Entry, IReadOnlyCollection<string> Sha1s);

    public async Task<DbSyncResult> SyncAllAsync(CancellationToken cancellationToken)
    {
        var state = await games.ListSyncStateAsync(cancellationToken).ConfigureAwait(false);
        var rowByPath = state.ToDictionary(s => s.ZipPath, StringComparer.OrdinalIgnoreCase);
        var dbSha1s = (await roms.ListSha1sAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(r => r.GameId, r => r.Sha1);

        var skipped = new List<DbSyncSkipItem>();
        var kept = new HashSet<Guid>(); // rows left exactly as they are
        var found = new List<Found>();

        foreach (var (path, size, mtime) in ScanZips())
        {
            rowByPath.TryGetValue(path, out var row);
            if (row is not null && row.ZipSizeBytes == size && row.ZipMtimeUtc == mtime)
            {
                found.Add(new Found(path, row.Id, null, dbSha1s[row.Id].ToList()));
                continue;
            }

            var (entry, skip) = Read(path, size, mtime);
            if (entry is not null)
            {
                found.Add(new Found(path, entry.Info.GameId, entry, entry.Info.Roms.Select(r => r.Metadata.Sha1).ToList()));
                continue;
            }

            skipped.Add(skip!);
            if (row is not null)
            {
                kept.Add(row.Id);
                found.Add(new Found(path, row.Id, null, dbSha1s[row.Id].ToList()));
            }
        }

        foreach (var group in found.GroupBy(f => f.Id).Where(g => g.Count() > 1))
        {
            var where = string.Join(", ", group.Select(f => f.ZipPath + (f.Entry is null ? "" : $" (updatedAt {f.Entry.Info.Metadata.UpdatedAt:O})")));
            foreach (var f in group)
                skipped.Add(new DbSyncSkipItem(DbSyncSkip.DuplicateGameId, f.ZipPath, $"gameId {group.Key} at {where}"));
            kept.Add(group.Key);
        }

        // Rows that stay (unchanged or kept) already own their SHA1s; candidates claim theirs in zip_path order.
        var owner = new Dictionary<string, (Guid Id, string Path)>(StringComparer.Ordinal);
        foreach (var f in found.Where(f => f.Entry is null || kept.Contains(f.Id)))
            foreach (var sha1 in f.Sha1s)
                owner.TryAdd(sha1, (f.Id, f.ZipPath));

        var replace = new List<MirrorEntry>();
        foreach (var f in found.Where(f => f.Entry is not null && !kept.Contains(f.Id)).OrderBy(f => f.ZipPath, StringComparer.Ordinal))
        {
            var clash = f.Sha1s.Select(s => (Sha1: s, Owner: owner.GetValueOrDefault(s))).FirstOrDefault(c => c.Owner.Path is not null && c.Owner.Id != f.Id);
            if (clash.Owner.Path is not null)
            {
                skipped.Add(new DbSyncSkipItem(DbSyncSkip.DuplicateSha1, f.ZipPath, $"SHA1 {clash.Sha1} is also in {clash.Owner.Path}"));
                kept.Add(f.Id);
                continue;
            }
            foreach (var sha1 in f.Sha1s)
                owner[sha1] = (f.Id, f.ZipPath);
            replace.Add(f.Entry!);
        }

        var foundIds = found.Select(f => f.Id).ToHashSet();
        var existing = state.ToDictionary(s => s.Id);
        var delete = state.Where(s => !foundIds.Contains(s.Id) && !kept.Contains(s.Id)).ToList();

        var changes = replace
            .Select(e => new DbSyncChangeItem(existing.ContainsKey(e.Info.GameId) ? DbSyncChange.Updated : DbSyncChange.Inserted, e.Info.GameId, e.ZipPath))
            .Concat(delete.Select(s => new DbSyncChangeItem(DbSyncChange.Deleted, s.Id, s.ZipPath)))
            .ToList();

        if (changes.Count > 0)
            await mirror.ApplyAsync(replace, delete.Select(s => s.Id).ToList(), cancellationToken).ConfigureAwait(false);

        foreach (var change in changes)
            logger.LogInformation("DB sync: {Change} game {GameId} ({ZipPath})", change.Kind, change.GameId, change.ZipPath);
        foreach (var skip in skipped)
            logger.LogDebug("DB sync: skipped {ZipPath}: {Reason} {Details}", skip.ZipPath, skip.Reason, skip.Details);
        return new DbSyncResult(changes, skipped);
    }

    /// <summary>Every <c>.zip</c> at any depth under each registered <c>roms/&lt;Id&gt;/</c> (§12.1 scope).</summary>
    private IEnumerable<(string Path, long Size, DateTime Mtime)> ScanZips()
    {
        var root = paths.Value.Roms;
        foreach (var handler in systems.All)
        {
            var folder = new DirectoryInfo(Path.Combine(root, handler.Id));
            if (!folder.Exists)
                continue;
            foreach (var file in folder.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    yield return (Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.Length, MirrorEntry.WholeSeconds(file.LastWriteTimeUtc));
            }
        }
    }

    private (MirrorEntry? Entry, DbSyncSkipItem? Skip) Read(string path, long size, DateTime mtime)
    {
        try
        {
            using var zip = LibraryZip.Open(Path.Combine(paths.Value.Roms, path), reader);
            return zip.InfoJson switch
            {
                null => (null, new DbSyncSkipItem(DbSyncSkip.InfoJsonMissing, path, null)),
                { Kind: InfoJsonReadResult.Outcome.Ok, Game: { } game } => (new MirrorEntry(game, path, size, mtime), null),
                { Kind: InfoJsonReadResult.Outcome.NewerSchema } r => (null, new DbSyncSkipItem(DbSyncSkip.InfoJsonNewerSchema, path, $"schemaVersion {r.FileVersion}")),
                var r => (null, new DbSyncSkipItem(DbSyncSkip.InfoJsonInvalid, path, r.Rule)),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return (null, new DbSyncSkipItem(DbSyncSkip.ZipUnreadable, path, ex.Message));
        }
    }
}
