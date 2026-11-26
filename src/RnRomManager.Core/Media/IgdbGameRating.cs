namespace RnRomManager.Core.Media;

/// <summary>P3: one IGDB game's cover and rating; each null when IGDB has none.</summary>
public sealed record IgdbGameRating(
    int Id,
    string? CoverImageId,
    double? TotalRating,
    int? TotalRatingCount);
