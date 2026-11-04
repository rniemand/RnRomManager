namespace RnRomManager.Core.Library;

/// <summary>
/// <c>info.json</c> at the root of a library ZIP (spec §6.2, schema v1): the source of truth for a game. ✓ fields are
/// <c>required</c> and non-nullable, so a file missing one (or holding null) fails to deserialize.
/// </summary>
public sealed record InfoJsonGame
{
    public required int SchemaVersion { get; init; }
    public required Guid GameId { get; init; }

    /// <summary>Handler <c>Id</c> (<c>gb</c>, <c>gbc</c>).</summary>
    public required string System { get; init; }

    public required string Title { get; init; }
    public required string DisplayTitle { get; init; }
    public int? ReleaseYear { get; init; }
    public required IReadOnlyList<string> Developers { get; init; }
    public required IReadOnlyList<string> Publishers { get; init; }
    public required IReadOnlyList<string> Genres { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<InfoJsonRom> Roms { get; init; }
    public required IReadOnlyList<InfoJsonImage> Images { get; init; }
    public required InfoJsonGameMetadata Metadata { get; init; }
}
