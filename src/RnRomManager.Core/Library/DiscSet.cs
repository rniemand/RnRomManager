namespace RnRomManager.Core.Library;

/// <summary>Multi-disc M1: a game's discs that play together, in disc order, and (M2) the name of their playlist.</summary>
public sealed record DiscSet(string Name, IReadOnlyList<RomRow> Discs);
