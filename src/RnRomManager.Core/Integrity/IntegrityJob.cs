using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using static RnRomManager.Core.Integrity.IntegrityIssueType;

namespace RnRomManager.Core.Integrity;

/// <summary>
/// The <c>Integrity</c> job (spec §12). First the DB auto-repair through <see cref="DbSync"/> (recorded as
/// <c>AutoRepaired</c> issues, plus <c>DuplicateGameId</c>/<c>DuplicateSha1</c>), then every file under <c>roms/</c>:
/// files in the root and under unregistered folders are <c>OrphanFile</c> (never opened); under a registered system
/// folder every non-ZIP is an orphan and every <c>.zip</c> is checked. Quick opens a ZIP only when its DB row (as it was
/// before the repair) differs in size or whole-second mtime, else it checks just the location; Deep opens every ZIP and
/// also hashes every ROM and image entry. The checker never moves or deletes library files.
/// Findings update open issues with the same (type, zip_path, entry_name) or open new ones. A completed run resolves
/// open issues it didn't see, but only where it performed that check (D48); a cancelled run resolves nothing. Retention
/// (180 days) runs at the end of a completed run.
/// </summary>
public sealed class IntegrityJob(JobMode mode, IGameRepository games, DbSync dbSync, IIntegrityIssueRepository issues,
    IJobRepository jobs, InfoJsonReader reader, ISystemRegistry systems, IOptions<PathsOptions> paths, TimeProvider time,
    ILogger<IntegrityJob> logger) : IJob
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(180);

    /// <summary>Re-checked on every run, whatever was opened.</summary>
    private static readonly HashSet<string> GlobalTypes = [OrphanFile, DuplicateGameId, DuplicateSha1];

    private enum Checked
    {
        LocationOnly,
        Opened,
    }

    public JobType Type => JobType.Integrity;
    public JobMode? Mode => mode;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var before = (await games.ListAllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(g => g.ZipPath, StringComparer.OrdinalIgnoreCase);
        var sync = await dbSync.SyncAllAsync(cancellationToken).ConfigureAwait(false);

        var findings = new Dictionary<(string, string, string), IntegrityFinding>();
        void Add(IntegrityFinding finding) => findings.TryAdd(finding.Key, finding);

        var repairs = sync.Changes.Select(c => new IntegrityFinding(c.Kind switch
        {
            DbSyncChange.Inserted => DbRowInserted,
            DbSyncChange.Updated => DbRowUpdated,
            _ => DbRowDeleted,
        }, c.ZipPath, SystemId: SystemOf(c.ZipPath), GameId: c.GameId)).ToList();
        foreach (var skip in sync.Skipped.Where(s => s.Reason is DbSyncSkip.DuplicateGameId or DbSyncSkip.DuplicateSha1))
            Add(new IntegrityFinding(skip.Reason.ToString(), skip.ZipPath, SystemId: SystemOf(skip.ZipPath), Details: skip.Details));

        var (zips, orphans) = Scan();
        foreach (var orphan in orphans)
            Add(new IntegrityFinding(OrphanFile, orphan, SystemId: SystemOf(orphan)));

        context.Progress.SetTotal(zips.Count);
        var checkedZips = new Dictionary<string, Checked>(StringComparer.OrdinalIgnoreCase);
        var cancelled = false;
        var opened = 0;
        foreach (var zip in zips)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            context.Progress.SetCurrentItem(zip);
            before.TryGetValue(zip, out var row);
            var file = new FileInfo(Path.Combine(paths.Value.Roms, zip));
            var unchanged = row is not null && row.ZipSizeBytes == file.Length && row.ZipMtimeUtc == MirrorEntry.WholeSeconds(file.LastWriteTimeUtc);
            if (mode == JobMode.Quick && unchanged)
            {
                CheckLocation(zip, row!.SystemId, row.Title, row.Id, Add);
                checkedZips[zip] = Checked.LocationOnly;
            }
            else
            {
                await CheckZipAsync(zip, row, Add, cancellationToken).ConfigureAwait(false);
                checkedZips[zip] = Checked.Opened;
                opened++;
            }
            context.Progress.ItemDone();
        }

        var now = time.GetUtcNow().UtcDateTime;
        var (added, resolved) = await ApplyAsync(context.JobId, findings.Values, repairs, zips, checkedZips, resolve: !cancelled, now)
            .ConfigureAwait(false);

        int purgedJobs = 0, purgedIssues = 0;
        if (!cancelled)
        {
            // Retention is housekeeping: it must not be skipped by a late cancel.
            purgedJobs = await jobs.PurgeAsync(now - Retention, CancellationToken.None).ConfigureAwait(false);
            purgedIssues = await issues.PurgeAsync(now - Retention, CancellationToken.None).ConfigureAwait(false);
        }

        logger.LogInformation("Integrity {Mode}: {Zips} ZIPs ({Opened} opened), {Issues} issues ({New} new, {Resolved} resolved), {Repairs} DB repairs",
            mode, checkedZips.Count, opened, findings.Count, added, resolved, repairs.Count);
        return new
        {
            zips = checkedZips.Count, opened, issues = findings.Count, newIssues = added, resolved, repaired = repairs.Count, purgedJobs, purgedIssues,
        };
    }

    /// <summary>Library ZIPs under registered system folders, and every other file under <c>roms/</c> (relative, <c>/</c>).</summary>
    private (List<string> Zips, List<string> Orphans) Scan()
    {
        var root = paths.Value.Roms;
        var zips = new List<string>();
        var orphans = new List<string>();
        if (!Directory.Exists(root))
            return (zips, orphans);
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            var slash = relative.IndexOf('/');
            if (slash < 0)
            {
                // The startup write probe lives in the root for a moment (§4.1 step 4).
                if (relative != StorageFolders.ProbeFileName)
                    orphans.Add(relative);
                continue;
            }
            var registered = systems.ById(relative[..slash]) is not null;
            if (registered && relative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                zips.Add(relative);
            else
                orphans.Add(relative);
        }
        zips.Sort(StringComparer.Ordinal);
        orphans.Sort(StringComparer.Ordinal);
        return (zips, orphans);
    }

    private async Task CheckZipAsync(string zipPath, GameRow? row, Action<IntegrityFinding> add, CancellationToken ct)
    {
        var system = SystemOf(zipPath);
        LibraryZip zip;
        try
        {
            zip = LibraryZip.Open(Path.Combine(paths.Value.Roms, zipPath), reader);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            add(new IntegrityFinding(ZipUnreadable, zipPath, SystemId: system, GameId: row?.Id, Details: ex.Message));
            if (row is not null)
                CheckLocation(zipPath, row.SystemId, row.Title, row.Id, add);
            return;
        }

        using (zip)
        {
            var problem = zip.InfoJson switch
            {
                null => new IntegrityFinding(InfoJsonMissing, zipPath, SystemId: system, GameId: row?.Id),
                { Kind: InfoJsonReadResult.Outcome.Ok } => null,
                { Kind: InfoJsonReadResult.Outcome.NewerSchema } r =>
                    new IntegrityFinding(InfoJsonNewerSchema, zipPath, SystemId: system, GameId: row?.Id, Details: $"schemaVersion {r.FileVersion}"),
                var r => new IntegrityFinding(InfoJsonInvalid, zipPath, SystemId: system, GameId: row?.Id, Details: r.Rule),
            };
            if (problem is not null)
            {
                add(problem);
                if (row is not null)
                    CheckLocation(zipPath, row.SystemId, row.Title, row.Id, add);
                return;
            }

            var game = zip.InfoJson!.Game!;
            CheckLocation(zipPath, game.System, game.Title, game.GameId, add);
            IntegrityFinding Finding(string type, string? entry = null, string? details = null) =>
                new(type, zipPath, entry, game.System, game.GameId, details);

            var listed = game.Roms.Select(r => (Entry: LibraryZip.RomEntry(r.FileName), r.Metadata.SizeBytes, r.Metadata.Sha1))
                .Concat(game.Images.Select(i => (Entry: LibraryZip.ImageEntry(i.FileName), i.SizeBytes, i.Sha1)))
                .ToList();
            foreach (var (entry, size, sha1) in listed)
            {
                ct.ThrowIfCancellationRequested();
                if (zip.EntryLength(entry) is not { } length)
                {
                    add(Finding(EntryMissing, entry));
                    continue;
                }
                if (length != size)
                    add(Finding(EntrySizeMismatch, entry, $"entry {length} bytes, info.json {size}"));
                if (mode == JobMode.Deep && await HashAsync(zip, entry, length, ct).ConfigureAwait(false) is var (actual, error) && actual != sha1)
                    add(Finding(HashMismatch, entry, error ?? $"SHA1 {actual}, info.json {sha1}"));
            }

            var names = listed.Select(l => l.Entry).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in zip.EntryNames.Where(e => e != LibraryZip.InfoJsonEntry && !names.Contains(e)))
                add(Finding(EntryUnlisted, entry));

            var boxArt = game.Images.FirstOrDefault(i => i.Type == ImageType.BoxArt);
            if (boxArt is null)
                add(Finding(MissingBoxArt));
            else if (boxArt.IsDefault)
                add(Finding(DefaultBoxArt));
        }
    }

    /// <returns>The entry's SHA1, or null plus why it couldn't be read.</returns>
    private static async Task<(string? Sha1, string? Error)> HashAsync(LibraryZip zip, string entry, long length, CancellationToken ct)
    {
        try
        {
            await using var stream = zip.OpenEntry(entry, length);
            return (Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false)), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or LimitExceededException)
        {
            return (null, $"unreadable: {ex.Message}");
        }
    }

    /// <summary>§12.2 <c>WrongLocation</c>: system/bucket/name ≠ §6.1 (case-insensitive, §5); a clash suffix ` (N)` is accepted.</summary>
    private static void CheckLocation(string zipPath, string systemId, string title, Guid gameId, Action<IntegrityFinding> add)
    {
        var parts = zipPath.Split('/');
        var name = parts[^1];
        var stem = FileNameSanitizer.Sanitize(title);
        var nameOk = name.Equals(stem + ".zip", StringComparison.OrdinalIgnoreCase) || IsClashSuffixed(name, stem);
        var ok = parts.Length == 3 && nameOk
            && parts[0].Equals(systemId, StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals(Bucket.For(name), StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            var expected = $"{systemId}/{Bucket.For(stem + ".zip")}/{stem}.zip";
            add(new IntegrityFinding(WrongLocation, zipPath, SystemId: systemId, GameId: gameId, Details: $"expected {expected}"));
        }
    }

    private static bool IsClashSuffixed(string name, string stem)
    {
        var prefix = stem + " (";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(").zip", StringComparison.OrdinalIgnoreCase))
            return false;
        var number = name[prefix.Length..^").zip".Length];
        return int.TryParse(number, System.Globalization.NumberStyles.None, null, out var n) && n >= 2 && number[0] != '0';
    }

    private async Task<(int Added, int Resolved)> ApplyAsync(Guid jobId, IEnumerable<IntegrityFinding> findings, IReadOnlyList<IntegrityFinding> repairs,
        IReadOnlyList<string> zips, IReadOnlyDictionary<string, Checked> checkedZips, bool resolve, DateTime now)
    {
        // Not cancellable: what this run found is recorded even when it was cancelled part-way.
        var ct = CancellationToken.None;
        var open = await issues.ListAllOpenAsync(ct).ConfigureAwait(false);
        var openByKey = open.GroupBy(i => IntegrityFinding.KeyOf(i.Type, i.ZipPath, i.EntryName)).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<long>();
        var added = 0;

        foreach (var finding in findings)
        {
            var severity = SeverityOf(finding.Type);
            if (openByKey.TryGetValue(finding.Key, out var existing))
            {
                seen.Add(existing.Id);
                await issues.TouchAsync(existing.Id, jobId, severity, finding.Details, finding.GameId, ct).ConfigureAwait(false);
                continue;
            }
            await issues.InsertAsync(ToIssue(finding, IntegrityIssue.Open, jobId, now, resolvedAt: null), ct).ConfigureAwait(false);
            added++;
            Log(finding, severity);
        }
        foreach (var repair in repairs)
            await issues.InsertAsync(ToIssue(repair, IntegrityIssue.AutoRepaired, jobId, now, resolvedAt: now), ct).ConfigureAwait(false);

        if (!resolve)
            return (added, 0);
        var onDisk = zips.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolved = open.Where(i => !seen.Contains(i.Id) && WasChecked(i, onDisk, checkedZips)).Select(i => i.Id).ToList();
        await issues.ResolveAsync(resolved, now, ct).ConfigureAwait(false);
        return (added, resolved.Count);
    }

    /// <summary>D48: did this run perform the check that would have found <paramref name="issue"/> again?</summary>
    private bool WasChecked(IntegrityIssue issue, HashSet<string> onDisk, IReadOnlyDictionary<string, Checked> checkedZips)
    {
        if (GlobalTypes.Contains(issue.Type) || issue.ZipPath is null)
            return true;
        if (!onDisk.Contains(issue.ZipPath))
            return true; // the ZIP is gone (its DB rows were repaired)
        return checkedZips.GetValueOrDefault(issue.ZipPath) switch
        {
            Checked.Opened => issue.Type != HashMismatch || mode == JobMode.Deep,
            Checked.LocationOnly => issue.Type == WrongLocation,
            _ => false,
        };
    }

    private static IntegrityIssue ToIssue(IntegrityFinding f, string status, Guid jobId, DateTime now, DateTime? resolvedAt) => new()
    {
        Type = f.Type,
        Severity = SeverityOf(f.Type),
        SystemId = f.SystemId,
        ZipPath = f.ZipPath,
        GameId = f.GameId,
        EntryName = f.EntryName,
        Details = f.Details,
        Status = status,
        FirstSeenJobId = jobId,
        LastSeenJobId = jobId,
        DetectedAt = now,
        ResolvedAt = resolvedAt,
    };

    private void Log(IntegrityFinding f, string severity)
    {
        var level = severity switch { IssueSeverity.Error => LogLevel.Error, IssueSeverity.Warn => LogLevel.Warning, _ => LogLevel.Information };
        logger.Log(level, "Integrity: {Type} {ZipPath} {Entry} {Details}", f.Type, f.ZipPath, f.EntryName, f.Details);
    }

    /// <summary>The first path segment when it's a registered system folder.</summary>
    private string? SystemOf(string zipPath)
    {
        var slash = zipPath.IndexOf('/');
        return slash > 0 && systems.ById(zipPath[..slash]) is { } handler ? handler.Id : null;
    }
}
