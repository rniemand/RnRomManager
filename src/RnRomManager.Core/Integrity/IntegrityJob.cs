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
public sealed class IntegrityJob(JobMode mode, IGameRepository games, IRomRepository roms, DbSync dbSync, IIntegrityIssueRepository issues,
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

    /// <summary>Test seams of the external file checks.</summary>
    public IntegrityHooks? Hooks { get; init; }

    /// <summary>B15: the one ZIP (relative to <c>Paths:Roms</c>, <c>/</c>) a <see cref="JobMode.Game"/> run checks.</summary>
    public string? ZipPath { get; set; }

    /// <summary>Opens and hashes everything it checks: Deep, and a Game check of one ZIP.</summary>
    private bool Hashes => mode is JobMode.Deep or JobMode.Game;

    public JobType Type => JobType.Integrity;
    public JobMode? Mode => mode;

    public async Task<object?> RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        if (mode == JobMode.Game)
            return await RunGameAsync(context, cancellationToken).ConfigureAwait(false);
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
        // G4/E4: a path nothing could be opened at (unsafe, ambiguous, or equal to another ZIP for the database) is
        // reported once, never opened, and keeps its issues.
        var notChecked = sync.Skipped.Where(s => s.NotChecked).ToList();
        foreach (var skip in notChecked)
            Add(new IntegrityFinding(ZipUnreadable, skip.ZipPath, SystemId: SystemOf(skip.ZipPath),
                GameId: before.TryGetValue(skip.ZipPath, out var behind) ? behind.Id : null, Details: skip.Details));

        // G1/G3: the external files as the DB lists them after the repair (one read; a cancel stops the scan below instead,
        // so the run still records what it found).
        var external = await roms.ListExternalAsync(CancellationToken.None).ConfigureAwait(false);
        var externalByZip = external.ToLookup(r => r.ZipPath, StringComparer.OrdinalIgnoreCase);
        var listedFiles = external.Select(FileKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var checks = new ExternalChecks(externalByZip, Claimants(external),
            sync.Skipped.Select(k => k.ZipPath).ToHashSet(StringComparer.OrdinalIgnoreCase), [.. notChecked.Select(k => k.ZipPath)]);

        var (zips, orphans) = Scan(listedFiles);
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
                CheckLocation(zip, row!.SystemId, row.Title, row.Id, Add);
                checkedZips[zip] = Checked.LocationOnly;
            }
            else
            {
                await CheckZipAsync(zip, row, Add, cancellationToken).ConfigureAwait(false);
                checkedZips[zip] = Checked.Opened;
                opened++;
            }
            try
            {
                await CheckExternalAsync(zip, externalByZip[zip], checks, Add, context.Progress, cancellationToken).ConfigureAwait(false);
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

        var now = time.GetUtcNow().UtcDateTime;
        var (added, resolved) = await ApplyAsync(context.JobId, findings.Values, repairs, zips, checkedZips, checks, resolve: !cancelled, now)
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

    private static string FileKey(ExternalRomRow r) => r.ZipPath[..(r.ZipPath.LastIndexOf('/') + 1)] + r.ExternalFileName;

    /// <summary>G4: a file two games claim (same folder and name, ignoring case) belongs to neither.</summary>
    private static Dictionary<string, List<Guid>> Claimants(IEnumerable<ExternalRomRow> external) =>
        external.GroupBy(FileKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r.GameId).Distinct().Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(r => r.GameId).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);

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
        var checks = new ExternalChecks(external.ToLookup(r => r.ZipPath, StringComparer.OrdinalIgnoreCase), Claimants(external), [], []);
        var findings = new Dictionary<(string, string, string), IntegrityFinding>();
        void Add(IntegrityFinding finding) => findings.TryAdd(finding.Key, finding);

        context.Progress.SetTotal(1);
        context.Progress.SetCurrentItem(zip);
        var cancelled = cancellationToken.IsCancellationRequested;
        if (!cancelled)
        {
            await CheckZipAsync(zip, row, Add, cancellationToken).ConfigureAwait(false);
            try
            {
                await CheckExternalAsync(zip, checks.ByZip[zip], checks, Add, context.Progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            context.Progress.ItemDone();
        }
        cancelled |= cancellationToken.IsCancellationRequested;

        var (added, resolved) = await ApplyAsync(context.JobId, findings.Values, [], [zip],
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

    private async Task CheckZipAsync(string zipPath, GameRow? row, Action<IntegrityFinding> add, CancellationToken ct)
    {
        var system = SystemOf(zipPath);
        LibraryZip zip;
        try
        {
            zip = OpenChecked(zipPath);
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

            // G1: an external ROM has no roms/ entry; its file is checked by StatExternal.
            var listed = game.Roms.Where(r => r.ExternalFileName is null).Select(r => (Entry: LibraryZip.RomEntry(r.FileName), r.Metadata.SizeBytes, r.Metadata.Sha1))
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
                if (Hashes && await HashAsync(zip, entry, length, ct).ConfigureAwait(false) is var (actual, error) && actual != sha1)
                    add(Finding(HashMismatch, entry, error ?? $"SHA1 {actual}, info.json {sha1}"));
            }

            var names = listed.Select(l => l.Entry).ToHashSet(StringComparer.Ordinal);
            // Notes of listed ROMs are expected (B10); orphan notes are still reported.
            names.UnionWith(game.Roms.Select(r => LibraryZip.NoteEntry(r.FileName)));
            foreach (var entry in zip.EntryNames.Where(e => e != LibraryZip.InfoJsonEntry && !names.Contains(e)))
                add(Finding(EntryUnlisted, entry));

            // v3: the codes are about the cover (I8), whatever its type.
            var cover = game.Images.FirstOrDefault(i => i.IsCover);
            if (cover is null)
                add(Finding(MissingBoxArt));
            else if (cover.IsDefault)
                add(Finding(DefaultBoxArt));
        }
    }

    /// <summary>
    /// G1/G2/G4: each external file the DB lists under <paramref name="zipPath"/> is checked beside the ZIP; the entry
    /// name is the file name. A file another game also claims is <c>EntryMissing</c> for each owner and isn't read. A
    /// path that isn't a plain file (a link, a folder) can't be used, so it is missing; one whose stat fails is only
    /// logged. Deep also hashes the file through a checked handle that must be the file that was statted, before and
    /// after hashing; a file replaced or changed meanwhile is logged and its size and hash results are dropped.
    /// </summary>
    private async Task CheckExternalAsync(string zipPath, IEnumerable<ExternalRomRow> rows, ExternalChecks checks,
        Action<IntegrityFinding> add, JobProgress progress, CancellationToken ct)
    {
        var root = paths.Value.Roms;
        var folder = Path.GetDirectoryName(Path.Combine(root, zipPath))!;
        var folderKey = zipPath[..(zipPath.LastIndexOf('/') + 1)];
        foreach (var rom in rows)
        {
            IntegrityFinding Finding(string type, string? details = null) => new(type, zipPath, rom.ExternalFileName, SystemOf(zipPath), rom.GameId, details);
            // G2a: what this run checked for the pointer; a pointer it couldn't check keeps its issues.
            void Checked(bool hashed = false) => checks.Mark(zipPath, rom.ExternalFileName, hashed);
            if (checks.Claimants.TryGetValue(folderKey + rom.ExternalFileName, out var owners))
            {
                add(Finding(EntryMissing, $"also claimed by game {string.Join(", ", owners.Where(o => o != rom.GameId))}"));
                Checked();
                continue;
            }
            var path = Path.Combine(folder, rom.ExternalFileName);
            var file = ExternalRomPath.ClassifyFile(root, path);
            switch (file.Status)
            {
                case ExternalPathStatus.Missing:
                    add(Finding(EntryMissing));
                    Checked();
                    continue;
                case ExternalPathStatus.Unreadable:
                    logger.LogWarning("Integrity: {Path} can't be checked: {Details}", file.Path, file.Details);
                    continue;
                case not ExternalPathStatus.Found:
                    add(Finding(EntryMissing, file.Details));
                    Checked();
                    continue;
            }
            var snapshot = file.Snapshot!;
            var size = snapshot.Length == rom.SizeBytes ? null : Finding(EntrySizeMismatch, $"file {snapshot.Length} bytes, info.json {rom.SizeBytes}");
            if (!Hashes)
            {
                if (size is not null)
                    add(size);
                Checked();
                continue;
            }

            Hooks?.BeforeExternalHash?.Invoke(file.Path);
            var opened = ExternalRomPath.OpenRead(root, file.Path, expected: snapshot);
            string? sha1 = null, error = null;
            switch (opened.Status)
            {
                case ExternalPathStatus.Missing:
                    add(Finding(EntryMissing));
                    Checked();
                    continue;
                case ExternalPathStatus.Unreadable:
                    error = $"unreadable: {opened.Details}";
                    break;
                case ExternalPathStatus.Found:
                    await using (var stream = opened.Stream!)
                    {
                        progress.BeginStep(JobStepPhases.Hashing, snapshot.Length, time.GetUtcNow().UtcDateTime);
                        try
                        {
                            sha1 = await Sha1Async(Hooks?.WrapHashStream?.Invoke(stream) ?? stream, progress, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            error = $"unreadable: {ex.Message}";
                        }
                        finally
                        {
                            progress.EndStep();
                        }
                        if (ExternalRomPath.SnapshotOf(stream) != snapshot)
                            sha1 = error = null; // changed in place while it was read
                    }
                    Hooks?.AfterExternalHash?.Invoke(file.Path);
                    if (sha1 is not null || error is not null)
                    {
                        var now = ExternalRomPath.ClassifyFile(root, file.Path);
                        if (now.Status == ExternalPathStatus.Missing)
                        {
                            add(Finding(EntryMissing));
                            Checked();
                            continue;
                        }
                        if (now.Snapshot != snapshot)
                            sha1 = error = null; // the path names another file by now
                    }
                    break;
            }
            if (sha1 is null && error is null)
            {
                logger.LogWarning("Integrity: {Path} changed while it was checked; it is checked again next run", file.Path);
                continue;
            }
            if (size is not null)
                add(size);
            if (sha1 != rom.Sha1)
                add(Finding(HashMismatch, error ?? $"SHA1 {sha1}, info.json {rom.Sha1}"));
            Checked(hashed: true);
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

    /// <summary>
    /// §12.2 <c>WrongLocation</c>: system/bucket/name ≠ §6.1 (case-insensitive, §5); a clash suffix ` (N)` is accepted.
    /// An external game's name is the budgeted one its builder gives the title (E2), suffixed or not.
    /// </summary>
    private void CheckLocation(string zipPath, string systemId, string title, Guid gameId, Action<IntegrityFinding> add)
    {
        var parts = zipPath.Split('/');
        var name = parts[^1];
        var external = systems.ById(systemId) is { EmbedRomsInZip: false };
        var stem = external ? Path.GetFileNameWithoutExtension(ExternalRomName.ZipName(title)) : FileNameSanitizer.Sanitize(title);
        var nameOk = external
            ? ExternalRomName.IsZipNameFor(title, name)
            : name.Equals(stem + ".zip", StringComparison.OrdinalIgnoreCase) || IsClashSuffixed(name, stem);
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
    private bool WasChecked(IntegrityIssue issue, HashSet<string> onDisk, IReadOnlyDictionary<string, Checked> checkedZips, ExternalChecks checks)
    {
        if (issue.ZipPath is { } path && checks.IsUnchecked(path) && issue.Type is not (DuplicateGameId or DuplicateSha1))
            return false; // G4: nothing at or below this path could be checked
        if (GlobalTypes.Contains(issue.Type) || issue.ZipPath is null)
            return true;
        // G2a: an external file's issue (the game row may be gone, so the issue's own system decides).
        if (IntegrityFinding.IsPointerIssue(issue.Type, issue.EntryName)
            && issue.SystemId is { } systemId && systems.ById(systemId) is { EmbedRomsInZip: false })
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
    private LibraryZip OpenChecked(string zipPath)
    {
        var opened = ExternalRomPath.OpenScanned(paths.Value.Roms, zipPath, FileShare.Read | FileShare.Delete);
        if (opened.Status != ExternalPathStatus.Found)
            throw new IOException(opened.Details);
        return LibraryZip.Open(new BufferedStream(opened.Stream!), opened.Path, reader);
    }

    /// <summary>The pointers a ZIP's <c>info.json</c> lists, or null when it can't be read.</summary>
    private HashSet<string>? ReadPointers(string zipPath)
    {
        try
        {
            using var zip = OpenChecked(zipPath);
            return zip.InfoJson is { Kind: InfoJsonReadResult.Outcome.Ok, Game: { } game }
                ? game.Roms.Select(r => r.ExternalFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The external file rows, claims and per-pointer outcomes of one run (G1, G2a, G4).</summary>
    private sealed class ExternalChecks(ILookup<string, ExternalRomRow> byZip, IReadOnlyDictionary<string, List<Guid>> claimants,
        HashSet<string> skippedBySync, IReadOnlyList<string> notChecked)
    {
        /// <summary>At or below a path DB sync could not open or enter (G4).</summary>
        public bool IsUnchecked(string path) => notChecked.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)
            || (path.Length > p.Length && path[p.Length] == '/' && path.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

        private readonly Dictionary<(string, string), bool> _outcomes = [];
        private readonly Dictionary<string, HashSet<string>?> _zipPointers = new(StringComparer.OrdinalIgnoreCase);

        public ILookup<string, ExternalRomRow> ByZip => byZip;
        public IReadOnlyDictionary<string, List<Guid>> Claimants => claimants;
        public HashSet<string> SkippedBySync => skippedBySync;

        private static (string, string) Key(string zipPath, string pointer) => (zipPath.ToUpperInvariant(), pointer.ToUpperInvariant());

        /// <summary>Records that the pointer's stat (and, with <paramref name="hashed"/>, its hash) was checked.</summary>
        public void Mark(string zipPath, string pointer, bool hashed) =>
            _outcomes[Key(zipPath, pointer)] = hashed || _outcomes.GetValueOrDefault(Key(zipPath, pointer));

        /// <returns>Null when not checked; else whether it was hashed too.</returns>
        public bool? Outcome(string zipPath, string pointer) =>
            _outcomes.TryGetValue(Key(zipPath, pointer), out var hashed) ? hashed : null;

        public HashSet<string>? ZipPointers(string zipPath, Func<string, HashSet<string>?> read)
        {
            if (!_zipPointers.TryGetValue(zipPath, out var pointers))
                _zipPointers[zipPath] = pointers = read(zipPath);
            return pointers;
        }
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

    /// <summary>The SHA1 of <paramref name="stream"/>, read in 1 MiB chunks that report the job's <c>Hashing</c> step (spec B2).</summary>
    private static async Task<string> Sha1Async(Stream stream, JobProgress progress, CancellationToken ct)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[1024 * 1024];
        long done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha1.AppendData(buffer, 0, read);
            done += read;
            progress.StepAdvanced(done);
        }
        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }
}

/// <summary>Test seams of <see cref="IntegrityJob"/>'s external file checks.</summary>
public sealed class IntegrityHooks
{
    /// <summary>Runs with the file's path after its stat, right before a Deep check opens it.</summary>
    public Action<string>? BeforeExternalHash { get; init; }

    /// <summary>Runs with the file's path after it was hashed and its handle closed, before the path is checked again.</summary>
    public Action<string>? AfterExternalHash { get; init; }

    /// <summary>Wraps the stream a Deep check hashes an external ROM from, e.g. to pause mid-read.</summary>
    public Func<Stream, Stream>? WrapHashStream { get; init; }
}
