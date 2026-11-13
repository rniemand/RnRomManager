using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Ingest;

/// <summary>B12: finds the unmatched bin/cue set a Review item belongs to, as ingest would plan it today.</summary>
public class ReviewSets(IngestFileScanner scanner, IIngestReviewRepository reviews, ISystemRegistry systems, IOptions<PathsOptions> paths,
    ILogger<ReviewSets> logger)
{
    /// <returns>
    /// The set's members in set order (the cue, then its <c>FILE</c>s), each with its review row; null when the item
    /// isn't a loose file of a cue-taking <c>FileSets</c> system, ingest wouldn't plan it into a complete set, a member
    /// has no <c>Pending</c> <c>NoDatMatch</c> row, or the folder or a cue can't be read.
    /// </returns>
    public async Task<IReadOnlyList<(IngestFile File, ReviewItem Row)>?> FindAsync(ReviewItem item, CancellationToken cancellationToken = default)
    {
        if (item.ArchivePath is not null || systems.ById(item.SystemId) is not { FileSets: true } system
            || !system.RomExtensions.Contains(".cue", StringComparer.OrdinalIgnoreCase))
            return null;
        IReadOnlyList<IngestFile> members;
        try
        {
            var files = scanner.ScanSystem(item.SystemId);
            var byName = files.ToDictionary(f => f.Name, StringComparer.Ordinal);
            var unit = IngestSetPlanner.Plan(files.Select(f => new PlannerFile(f.Name, f.SizeBytes, IngestCueReader.Read(paths.Value.Ingest, f, logger))).ToList())
                .FirstOrDefault(u => u.Kind == PlannedKind.Set && u.Members.Any(m => byName[m].FullPath == item.SourcePath));
            if (unit is null)
                return null;
            members = unit.Members.Select(m => byName[m]).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Review item {ReviewId}: its folder couldn't be read for a set", item.Id);
            return null;
        }
        var rows = (await reviews.ListPendingByPathsAsync(members.Select(m => m.FullPath).ToList(), cancellationToken).ConfigureAwait(false))
            .ToLookup(r => r.SourcePath, StringComparer.Ordinal);
        var found = new List<(IngestFile, ReviewItem)>();
        foreach (var member in members)
        {
            if (rows[member.FullPath].FirstOrDefault() is not { Reason: "NoDatMatch" } row)
                return null;
            found.Add((member, row));
        }
        return found;
    }
}
