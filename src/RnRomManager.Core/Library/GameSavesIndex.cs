namespace RnRomManager.Core.Library;

/// <summary>B2: the whole <c>saves/saves.json</c>.</summary>
public sealed record GameSavesIndex
{
    public required int Version { get; init; }
    public required IReadOnlyList<GameSave> Saves { get; init; }
}
