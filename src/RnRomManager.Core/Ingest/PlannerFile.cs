namespace RnRomManager.Core.Ingest;

/// <summary>One loose file of an ingest folder, as the planner sees it.</summary>
/// <param name="CueBytes">
/// For a <c>.cue</c>, <c>.toc</c>, <c>.ccd</c> or <c>.mds</c> of at most <see cref="IngestSetPlanner.MaxCueBytes"/>: its bytes, or null when it couldn't be read.
/// Ignored for other files.
/// </param>
public sealed record PlannerFile(string Name, long Size, byte[]? CueBytes = null);
