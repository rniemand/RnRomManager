namespace RnRomManager.Core.Library;

/// <summary>A row of <c>games</c> (spec §14.1): the DB mirror of a library ZIP's <c>info.json</c>.</summary>
public sealed record GameRow
{
    public required Guid Id { get; init; }
    public required string SystemId { get; init; }
    public required string Title { get; init; }
    public required string DisplayTitle { get; init; }

    /// <summary>Relative to <c>Paths:Roms</c>, <c>/</c> separators.</summary>
    public required string ZipPath { get; init; }

    public required string Bucket { get; init; }
    public int? ReleaseYear { get; init; }
    public required IReadOnlyList<string> Developers { get; init; }
    public required IReadOnlyList<string> Publishers { get; init; }
    public required IReadOnlyList<string> Genres { get; init; }
    public string? Description { get; init; }
    public bool HasDefaultBoxArt { get; init; }
    public int? Rating { get; init; }
    public bool Favourite { get; init; }
    public int? IgdbId { get; init; }

    /// <summary>The saves in the ZIP (its <c>saves/</c> entries but the index); set by the mirror, not from <c>info.json</c>.</summary>
    public int SaveCount { get; init; }

    public required long ZipSizeBytes { get; init; }

    /// <summary>Whole seconds, UTC (§12.1).</summary>
    public required DateTime ZipMtimeUtc { get; init; }

    public required int InfoSchemaVersion { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}
