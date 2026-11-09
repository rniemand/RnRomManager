namespace RnRomManager.Core.Collections;

/// <summary>
/// A user's game collection (B2). <see cref="GameCount"/> counts only games that still exist (B6, B17);
/// <see cref="CoverFormat"/> is <c>png</c>, <c>jpg</c> or null (B10).
/// </summary>
public sealed record Collection(
    Guid Id,
    int UserId,
    string Name,
    string? Notes,
    string? CoverFormat,
    DateTime? CoverUpdatedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long GameCount = 0);

/// <summary>A collection's id and name, for a game's collections (B16).</summary>
public sealed record CollectionRef(Guid Id, string Name);
