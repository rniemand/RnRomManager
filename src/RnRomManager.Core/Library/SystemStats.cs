namespace RnRomManager.Core.Library;

/// <summary>Per-system library counts (spec §23 <c>/api/systems</c>). <c>Regions</c> = distinct ROM regions, sorted.</summary>
public sealed record SystemStats(string SystemId, int GameCount, int RomCount, IReadOnlyList<string> Regions);
