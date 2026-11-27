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
/// also hashes every ROM and image entry. An external game's files are checked by stat after its ZIP, whether or not
/// the ZIP was opened (G1), and are not orphans (G3). The checker never moves or deletes library files.
/// Findings update open issues with the same (type, zip_path, entry_name) or open new ones. A completed run resolves
/// open issues it didn't see, but only where it performed that check (D48); a cancelled run resolves nothing. Retention
/// (180 days) runs at the end of a completed run.
/// </summary>
public sealed class IntegrityJob(
    JobMode mode,
    IGameRepository games,
    IRomRepository roms,
    DbSync dbSync,
    IIntegrityIssueRepository issues,
    IJobRepository jobs,
    InfoJsonReader reader,
    ISystemRegistry systems,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger<IntegrityJob> logger,
    IImageRepository images,
    CoverCache cache,
    ILibraryMirror mirror) : IJob
{
    /// <summary>C5: a <c>.tmp</c> file younger than this may belong to a generation in flight.</summary>
    public static readonly TimeSpan TmpGrace = TimeSpan.FromHours(1);

    public static readonly TimeSpan Retention = TimeSpan.FromDays(180);

    /// <summary>Re-checked on every run, whatever was opened.</summary>
    private static readonly HashSet<string> GlobalTypes = [OrphanFile, DuplicateGameId, DuplicateSha1];

    /// <summary>Test seams of the external file checks.</summary>
    public IntegrityHooks? Hooks { get; init; }

    /// <summary>B15: the one ZIP (relative to <c>Paths:Roms</c>, <c>/</c>) a <see cref="JobMode.Game"/> run checks.</summary>
    public string? ZipPath { get; set; }

    /// <summary>Opens and hashes everything it checks: Deep, and a Game check of one ZIP.</summary>
    private bool Hashes => mode is JobMode.Deep or JobMode.Game;

    private ZipIntegrityCheck ZipCheck => field ??= new(Hashes, systems, paths, reader, mirror);

    private ExternalIntegrityCheck ExternalCheck => field ??= new(Hashes, Hooks, systems, paths, time, logger);

    private ImageCacheSweep ImageCache => field ??= new(images, cache, paths, reader, time, logger);

    public JobType Type => JobType.Integrity;
    public JobMode? Mode => mode;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        if (mode == JobMode.Game)
            return await RunGameAsync(context, cancellationToken).ConfigureAwait(false);
        var before = (await games.ListAllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(g => g.ZipPath, StringComparer.OrdinalIgnoreCase);
        var sync = await dbSync.SyncAllAsync(cancellationToken).ConfigureAwait(false);
        // A5: housekeeping of rows whose game the repair (or a delete) removed; not skipped by a cancel.
        var orphanActions = await games.DeleteOrphanActionsAsync(CancellationToken.None).ConfigureAwait(false);
        if (Hashes)
            ZipCheck.CoverActions = await games.ListCoverActionsAsync(CancellationToken.None).ConfigureAwait(false);

        var findings = new Dictionary<(string, string, string), IntegrityFinding>();
        void Add(IntegrityFinding finding) => findings.TryAdd(finding.Key, finding);

        var repairs = sync.Changes.Select(c => new IntegrityFinding(c.Kind switch
        {
            DbSyncChange.Inserted => DbRowInserted,
            DbSyncChange.Updated => DbRowUpdated,
            _ => DbRowDeleted,
        }, c.ZipPath, SystemId: SystemOf(systems, c.ZipPath), GameId: c.GameId)).ToList();
        foreach (var skip in sync.Skipped.Where(s => s.Reason is DbSyncSkip.DuplicateGameId or DbSyncSkip.DuplicateSha1))
            Add(new IntegrityFinding(skip.Reason.ToString(), skip.ZipPath, SystemId: SystemOf(systems, skip.ZipPath), Details: skip.Details));
        // G4/E4: a path nothing could be opened at (unsafe, ambiguous, or equal to another ZIP for the database) is
        // reported once, never opened, and keeps its issues.
        var notChecked = sync.Skipped.Where(s => s.NotChecked).ToList();
        foreach (var skip in notChecked)
            Add(new IntegrityFinding(ZipUnreadable, skip.ZipPath, SystemId: SystemOf(systems, skip.ZipPath),
                GameId: before.TryGetValue(skip.ZipPath, out var behind) ? behind.Id : null, Details: skip.Details));

        // G1/G3: the external files as the DB lists them after the repair (one read; a cancel stops the scan below instead,
        // so the run still records what it found).
        var external = await roms.ListExternalAsync(CancellationToken.None).ConfigureAwait(false);
        var externalByZip = external.ToLookup(r => r.ZipPath, StringComparer.OrdinalIgnoreCase);
        var listedFiles = external.Select(ExternalIntegrityCheck.FileKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var checks = new ExternalChecks(externalByZip, ExternalIntegrityCheck.Claimants(external),
            sync.Skipped.Select(k => k.ZipPath).ToHashSet(StringComparer.OrdinalIgnoreCase), [.. notChecked.Select(k => k.ZipPath)]);

        var (zips, orphans) = Scan(listedFiles);
        foreach (var orphan in orphans)
            Add(new IntegrityFinding(OrphanFile, orphan, SystemId: SystemOf(systems, orphan)));

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
            if (checks.IsUnchecked(zip))
            {
                context.Progress.ItemDone();
                continue;
            }
            before.TryGetValue(zip, out var row);
            var file = new FileInfo(Path.Combine(paths.Value.Roms, zip));
            var unchanged = row is not null && row.ZipSizeBytes == file.Length && row.ZipMtimeUtc == MirrorEntry.WholeSeconds(file.LastWriteTimeUtc);
            if (mode == JobMode.Quick && unchanged)
            {
                ZipCheck.CheckLocation(zip, row!.SystemId, row.Title, row.Id, Add);
                checkedZips[zip] = Checked.LocationOnly;
            }
            else
            {
                await ZipCheck.CheckZipAsync(zip, row, Add, cancellationToken).ConfigureAwait(false);
                checkedZips[zip] = Checked.Opened;
                opened++;
            }
            try
            {
                await ExternalCheck.CheckExternalAsync(zip, externalByZip[zip], checks, Add, context.Progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // G2: what was found so far is recorded; nothing is resolved.
                cancelled = true;
                break;
            }
            context.Progress.ItemDone();
        }
        cancelled |= cancellationToken.IsCancellationRequested;

        var (cacheGenerated, cacheRemoved, cacheFailed) = cancelled ? (0, 0, 0) : await ImageCache.RefreshImageCacheAsync(cancellationToken).ConfigureAwait(false);
        cancelled |= cancellationToken.IsCancellationRequested;

        var now = time.GetUtcNow().UtcDateTime;
        repairs.AddRange(ZipCheck.ActionRepairs);
        var (added, resolved) = await ApplyAsync(context.JobId, findings.Values, repairs, zips, checkedZips, checks, resolve: !cancelled, now)
            .ConfigureAwait(false);

        int purgedJobs = 0, purgedIssues = 0;
        if (!cancelled)
        {
            // Retention is housekeeping: it must not be skipped by a late cancel.
            purgedJobs = await jobs.PurgeAsync(now - Retention, CancellationToken.None).ConfigureAwait(false);
            purgedIssues = await issues.PurgeAsync(now - Retention, CancellationToken.None).ConfigureAwait(false);
        }

        logger.LogInformation("Integrity {Mode}: {Zips} ZIPs ({Opened} opened), {Issues} issues ({New} new, {Resolved} resolved), {Repairs} DB repairs, " +
            "{OrphanActions} orphan action rows; image cache {Generated} generated, {Removed} removed, {Failed} failed",
            mode, checkedZips.Count, opened, findings.Count, added, resolved, repairs.Count, orphanActions, cacheGenerated, cacheRemoved, cacheFailed);
        return new
        {
            zips = checkedZips.Count, opened, issues = findings.Count, newIssues = added, resolved, repaired = repairs.Count, purgedJobs, purgedIssues,
            cacheGenerated, cacheRemoved, cacheFailed,
        };
    }

    /// <summary>
    /// B15: checks <see cref="ZipPath"/> only, as Deep would an opened ZIP (and its external files): no DB sync, scan,
    /// orphan or duplicate checks, or retention. A completed run resolves only that ZIP's open issues.
    /// </summary>
    private async Task<object?> RunGameAsync(JobContext context, CancellationToken cancellationToken)
    {
        var zip = ZipPath ?? throw new InvalidOperationException("A Game integrity check needs a ZIP path.");
        var file = new FileInfo(Path.Combine(paths.Value.Roms, zip));
        if (!file.Exists || file.LinkTarget is not null)
        {
            logger.LogWarning("Integrity Game: {Zip} is not a plain file; nothing checked", zip);
            return new { issues = 0, newIssues = 0, resolved = 0 };
        }

        // One quick read each; a cancel is checked below, so the run still ends with its (empty) result.
        var row = (await games.ListAllAsync(CancellationToken.None).ConfigureAwait(false))
            .FirstOrDefault(g => string.Equals(g.ZipPath, zip, StringComparison.OrdinalIgnoreCase));
        var external = await roms.ListExternalAsync(CancellationToken.None).ConfigureAwait(false);
        var checks = new ExternalChecks(external.ToLookup(r => r.ZipPath, StringComparer.OrdinalIgnoreCase), ExternalIntegrityCheck.Claimants(external), [], []);
        ZipCheck.CoverActions = await games.ListCoverActionsAsync(CancellationToken.None).ConfigureAwait(false);
        var findings = new Dictionary<(string, string, string), IntegrityFinding>();
        void Add(IntegrityFinding finding) => findings.TryAdd(finding.Key, finding);

        context.Progress.SetTotal(1);
        context.Progress.SetCurrentItem(zip);
        var cancelled = cancellationToken.IsCancellationRequested;
        if (!cancelled)
        {
            await ZipCheck.CheckZipAsync(zip, row, Add, cancellationToken).ConfigureAwait(false);
            try
            {
                await ExternalCheck.CheckExternalAsync(zip, checks.ByZip[zip], checks, Add, context.Progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            context.Progress.ItemDone();
        }
        cancelled |= cancellationToken.IsCancellationRequested;

        var (added, resolved) = await ApplyAsync(context.JobId, findings.Values, ZipCheck.ActionRepairs, [zip],
            new Dictionary<string, Checked>(StringComparer.OrdinalIgnoreCase) { [zip] = Checked.Opened }, checks, resolve: !cancelled,
            time.GetUtcNow().UtcDateTime, onlyZip: zip).ConfigureAwait(false);
        logger.LogInformation("Integrity Game {Zip}: {Issues} issues ({New} new, {Resolved} resolved), cancelled {Cancelled}",
            zip, findings.Count, added, resolved, cancelled);
        return new { issues = findings.Count, newIssues = added, resolved };
    }

    /// <summary>Library ZIPs under registered system folders, and every other file under <c>roms/</c> (relative, <c>/</c>).</summary>
    /// <param name="listedFiles">External files the DB lists (<c>&lt;ZIP folder&gt;/&lt;name&gt;</c>, ignoring case): not orphans.</param>
    private (List<string> Zips, List<string> Orphans) Scan(HashSet<string> listedFiles)
    {
        var root = paths.Value.Roms;
        var zips = new List<string>();
        var orphans = new List<string>();
        if (!Directory.Exists(root))
            return (zips, orphans);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(entry);
            if (!Directory.Exists(entry))
            {
                // The startup write probe lives in the root for a moment (§4.1 step 4).
                if (name != StorageFolders.ProbeFileName)
                    orphans.Add(name);
                continue;
            }
            // G4: links are never followed; DB sync reports what a system folder hides (ZipUnreadable).
            var registered = systems.ById(name) is not null;
            var scan = ExternalRomPath.ScanFolder(root, name);
            foreach (var file in scan.Files.Select(f => f.RelativePath))
            {
                if (registered && file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    zips.Add(file);
                else if (!registered || !listedFiles.Contains(file))
                    orphans.Add(file);
            }
            // A special file in a system folder hides no ZIP, so DB sync leaves it to this report.
            orphans.AddRange(scan.Problems.Where(p => !registered || (p.IsSpecialFile && !p.RelativePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.RelativePath));
        }
        zips.Sort(StringComparer.Ordinal);
        orphans.Sort(StringComparer.Ordinal);
        return (zips, orphans);
    }

    private async Task<(int Added, int Resolved)> ApplyAsync(Guid jobId, IEnumerable<IntegrityFinding> findings, IReadOnlyList<IntegrityFinding> repairs,
        IReadOnlyList<string> zips, IReadOnlyDictionary<string, Checked> checkedZips, ExternalChecks checks, bool resolve, DateTime now,
        string? onlyZip = null)
    {
        // Not cancellable: what this run found is recorded even when it was cancelled part-way.
        var ct = CancellationToken.None;
        var open = await issues.ListAllOpenAsync(ct).ConfigureAwait(false);
        // B15: a game check touches and resolves only that ZIP's own issues, never the global ones.
        if (onlyZip is not null)
            open = [.. open.Where(i => string.Equals(i.ZipPath, onlyZip, StringComparison.OrdinalIgnoreCase) && !GlobalTypes.Contains(i.Type))];
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
        var resolved = open.Where(i => !seen.Contains(i.Id) && WasChecked(i, onDisk, checkedZips, checks)).Select(i => i.Id).ToList();
        await issues.ResolveAsync(resolved, now, ct).ConfigureAwait(false);
        return (added, resolved.Count);
    }

    /// <summary>D48: did this run perform the check that would have found <paramref name="issue"/> again?</summary>
    private bool WasChecked(
        IntegrityIssue issue,
        HashSet<string> onDisk,
        IReadOnlyDictionary<string, Checked> checkedZips,
        ExternalChecks checks)
    {
        if (issue.ZipPath is { } path && checks.IsUnchecked(path) && issue.Type is not (DuplicateGameId or DuplicateSha1))
            return false; // G4: nothing at or below this path could be checked
        if (GlobalTypes.Contains(issue.Type) || issue.ZipPath is null)
            return true;
        // G2a: an external file's issue (the game row may be gone, so the issue's own system decides).
        if (IntegrityFinding.IsPointerIssue(issue.Type, issue.EntryName)
            && issue.SystemId is { } systemId && systems.ById(systemId) is { } pointerSystem && RomStorage.MayHoldExternalFiles(pointerSystem))
            return PointerWasChecked(issue, onDisk, checks);
        if (!onDisk.Contains(issue.ZipPath))
            return true; // the ZIP is gone (its DB rows were repaired)
        return checkedZips.GetValueOrDefault(issue.ZipPath) switch
        {
            Checked.Opened => issue.Type != HashMismatch || Hashes,
            Checked.LocationOnly => issue.Type == WrongLocation,
            _ => false,
        };
    }

    /// <summary>
    /// G2a for one external file's issue: resolvable when this run checked that pointer (a hash only in Deep), when its
    /// ZIP is gone, or when its ZIP and the mirror agree that the pointer is no longer listed.
    /// </summary>
    private bool PointerWasChecked(IntegrityIssue issue, HashSet<string> onDisk, ExternalChecks checks)
    {
        var needsHash = issue.Type == HashMismatch;
        if (needsHash && !Hashes)
            return false;
        if (checks.Outcome(issue.ZipPath!, issue.EntryName!) is { } hashed)
            return hashed || !needsHash;
        if (!onDisk.Contains(issue.ZipPath!))
            return true; // the ZIP is gone or moved (DB repair pruned or relocated its rows)
        var mirrored = checks.ByZip[issue.ZipPath!].Select(r => r.ExternalFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mirrored.Contains(issue.EntryName!))
            return false; // still listed, but this run couldn't check it
        // No longer mirrored: the ZIP must be readable, synced and list exactly the mirrored pointers.
        return !checks.SkippedBySync.Contains(issue.ZipPath!) && checks.ZipPointers(issue.ZipPath!, ReadPointers) is { } listed
            && listed.SetEquals(mirrored);
    }

    /// <summary>G4: a scanned ZIP, opened without following a link swapped in since the scan.</summary>
    /// <exception cref="IOException">It is no longer a safe regular file.</exception>
    internal static LibraryZip OpenChecked(string roms, string zipPath, InfoJsonReader reader)
    {
        var opened = ExternalRomPath.OpenScanned(roms, zipPath, FileShare.Read | FileShare.Delete);
        if (opened.Status != ExternalPathStatus.Found)
            throw new IOException(opened.Details);
        return LibraryZip.Open(new BufferedStream(opened.Stream!), opened.Path, reader);
    }

    /// <summary>The pointers a ZIP's <c>info.json</c> lists, or null when it can't be read.</summary>
    private HashSet<string>? ReadPointers(string zipPath)
    {
        try
        {
            using var zip = OpenChecked(paths.Value.Roms, zipPath, reader);
            return zip.InfoJson is { Kind: InfoJsonReadResultOutcome.Ok, Game: { } game }
                ? game.ExternalFileNames().ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IntegrityIssue ToIssue(
        IntegrityFinding f,
        string status,
        Guid jobId,
        DateTime now,
        DateTime? resolvedAt) => new()
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
    internal static string? SystemOf(ISystemRegistry systems, string zipPath)
    {
        var slash = zipPath.IndexOf('/');
        return slash > 0 && systems.ById(zipPath[..slash]) is { } handler ? handler.Id : null;
    }
}
