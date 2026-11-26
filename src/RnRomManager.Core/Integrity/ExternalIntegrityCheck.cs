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
/// <summary>The checks of a game's external files (G1–G4) for <see cref="IntegrityJob"/>.</summary>
internal sealed class ExternalIntegrityCheck(
    bool hashes,
    IntegrityHooks? hooks,
    ISystemRegistry systems,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger logger)
{
    internal static string FileKey(ExternalRomRow r) => r.ZipPath[..(r.ZipPath.LastIndexOf('/') + 1)] + r.ExternalFileName;

    /// <summary>G4: a file two games claim (same folder and name, ignoring case) belongs to neither.</summary>
    internal static Dictionary<string, List<Guid>> Claimants(IEnumerable<ExternalRomRow> external) =>
        external.GroupBy(FileKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r.GameId).Distinct().Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(r => r.GameId).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// G1/G2/G4: each external file the DB lists under <paramref name="zipPath"/> is checked beside the ZIP; the entry
    /// name is the file name. A file another game also claims is <c>EntryMissing</c> for each owner and isn't read. A
    /// path that isn't a plain file (a link, a folder) can't be used, so it is missing; one whose stat fails is only
    /// logged. Deep also hashes the file through a checked handle that must be the file that was statted, before and
    /// after hashing; a file replaced or changed meanwhile is logged and its size and hash results are dropped.
    /// </summary>
    internal async Task CheckExternalAsync(
        string zipPath,
        IEnumerable<ExternalRomRow> rows,
        ExternalChecks checks,
        Action<IntegrityFinding> add,
        JobProgress progress,
        CancellationToken ct)
    {
        var root = paths.Value.Roms;
        var folder = Path.GetDirectoryName(Path.Combine(root, zipPath))!;
        var folderKey = zipPath[..(zipPath.LastIndexOf('/') + 1)];
        foreach (var rom in rows)
        {
            IntegrityFinding Finding(string type, string? details = null) => new(type, zipPath, rom.ExternalFileName, IntegrityJob.SystemOf(systems, zipPath), rom.GameId, details);
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
            if (!hashes)
            {
                if (size is not null)
                    add(size);
                Checked();
                continue;
            }

            hooks?.BeforeExternalHash?.Invoke(file.Path);
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
                            sha1 = await Sha1Async(hooks?.WrapHashStream?.Invoke(stream) ?? stream, progress, ct).ConfigureAwait(false);
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
                    hooks?.AfterExternalHash?.Invoke(file.Path);
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
