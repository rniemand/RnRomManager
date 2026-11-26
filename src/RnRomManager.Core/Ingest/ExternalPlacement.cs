namespace RnRomManager.Core.Ingest;

/// <summary>An external ROM's file and the name it takes next to its ZIP (E1).</summary>
internal sealed record ExternalPlacement(ExternalSource Source, string ExternalFileName);
