namespace RnRomManager.Core.Library;

/// <summary>
/// A validated release edit (B4, <c>PATCH /api/games/{id}/roms/{romId}</c>): null = leave unchanged. <c>revision</c>,
/// <c>disc</c> and <c>version</c> can be cleared, so they carry a <c>Set…</c> flag, as <see cref="GamePatch"/> does.
/// </summary>
public sealed record ReleaseEdit
{
    public const string KindRelease = "release";
    public const string KindUpdate = "update";
    public const string KindDlc = "dlc";

    /// <summary><see cref="KindRelease"/>, <see cref="KindUpdate"/> or <see cref="KindDlc"/>.</summary>
    public string? Kind { get; init; }

    /// <summary>≤ 10 distinct non-empty values; may be empty (no region).</summary>
    public IReadOnlyList<string>? Regions { get; init; }

    /// <summary>≤ 20 distinct codes of 1–16 letters or <c>-</c>.</summary>
    public IReadOnlyList<string>? Languages { get; init; }

    public bool SetRevision { get; init; }

    /// <summary>Trimmed, ≤ 32 chars, no parentheses; null clears it.</summary>
    public string? Revision { get; init; }

    public bool SetDisc { get; init; }

    /// <summary>1–99 (disc systems only); null clears it.</summary>
    public int? Disc { get; init; }

    public bool? Hack { get; init; }
    public bool? Translated { get; init; }

    public bool SetVersion { get; init; }

    /// <summary>A dotted version (updates only); null clears it.</summary>
    public string? Version { get; init; }
}
