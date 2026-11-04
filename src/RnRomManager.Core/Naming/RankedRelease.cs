namespace RnRomManager.Core.Naming;

/// <summary>What <see cref="PrimaryReleaseRanker"/> needs to know about one ROM of a game.</summary>
/// <param name="Flags">§6.1.1 flags plus the app-added <c>Hack</c>, <c>Translated</c>, <c>Unverified</c>.</param>
public sealed record RankedRelease(
    string FileName,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Languages,
    string? Revision,
    IReadOnlyList<string> Flags);
