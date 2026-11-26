namespace RnRomManager.Core.Library;

/// <summary>Game counts per system id, region and letter bucket (F2); a multi-region game counts once per region.</summary>
public sealed record GameFacets(
    IReadOnlyDictionary<string, int> Systems, IReadOnlyDictionary<string, int> Regions, IReadOnlyDictionary<string, int> Letters);
