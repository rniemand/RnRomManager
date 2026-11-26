namespace RnRomManager.Core.Media;

/// <summary>
/// L4: one IGDB game for <b>Lookup Info</b>, already mapped to the Edit Metadata form's rules. <see cref="CoverUrl"/> is the
/// list thumbnail (t_cover_small); <see cref="CoverImageUrl"/> is the same cover at the size Suggest Images stores (t_1080p).
/// </summary>
public sealed record GameInfoMatch(
    int IgdbId,
    string Name,
    int? ReleaseYear,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Genres,
    string? Description,
    string? CoverUrl,
    string? CoverImageUrl,
    string? Url);
