namespace RnRomManager.Core.Ingest;

/// <summary>RV3: optional review-list filters; null fields are not applied. <paramref name="Query"/> is a trimmed substring of the file name or header title.</summary>
public sealed record ReviewFilter(string? SystemId = null, string? Reason = null, string? Query = null);
