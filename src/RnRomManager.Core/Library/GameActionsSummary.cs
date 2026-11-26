namespace RnRomManager.Core.Library;

/// <summary>T2: linked games (<c>igdb_id &gt; 0</c>), those without a pulled cover, and the latest rating refresh (UTC).</summary>
public sealed record GameActionsSummary(int IgdbLinked, int CoversPending, DateTime? LastIgdbSyncAt);
