namespace RnRomManager.Core.Library;

/// <summary>
/// A validated <c>PATCH /api/games/{id}</c> (spec §23): null = leave unchanged. <c>releaseYear</c> and
/// <c>description</c> can be cleared, so they carry a <c>Set…</c> flag.
/// </summary>
public sealed record GamePatch
{
    /// <summary>Trimmed, 1–255 chars, sanitizes to a non-empty name (§6.1).</summary>
    public string? Title { get; init; }

    public bool SetReleaseYear { get; init; }
    public int? ReleaseYear { get; init; }

    /// <summary>Trimmed and distinct, ≤ 20 items of 1–255 chars.</summary>
    public IReadOnlyList<string>? Developers { get; init; }

    public IReadOnlyList<string>? Publishers { get; init; }
    public IReadOnlyList<string>? Genres { get; init; }

    public bool SetDescription { get; init; }

    /// <summary>≤ 4000 chars; null clears it.</summary>
    public string? Description { get; init; }
}
