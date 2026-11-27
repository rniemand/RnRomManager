namespace RnRomManager.Core.Library;

/// <summary>
/// T2, AC8: linked games (<c>igdb_id &gt; 0</c>); those a default cover pull would process (neither a pulled nor a
/// checked time); those without a pulled cover; those without a stored IGDB rating; and the latest rating refresh (UTC).
/// </summary>
public sealed record GameActionsSummary(
    int IgdbLinked,
    int CoversPending,
    int CoversMissing,
    int RatingsMissing,
    DateTime? LastIgdbSyncAt);
