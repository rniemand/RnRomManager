namespace RnRomManager.Core.Ingest;

/// <summary>Arcade I3: whether a set ZIP holds a DAT set.</summary>
/// <param name="Missing">The missing required ROMs' names, in DAT order; empty when complete.</param>
/// <param name="SelfContained">The ZIP also holds the parent's (merged) ROMs and, if the set has BIOS-choice ROMs, one of them.</param>
public sealed record MameMatch(bool Complete, IReadOnlyList<string> Missing, bool SelfContained);
