namespace RnRomManager.Core.Library;

/// <summary>A row of <c>game_actions</c> (Admin actions A3). Times are UTC.</summary>
public sealed record GameActionsRow
{
    public DateTime? CoverImagePulledAt { get; init; }
    public DateTime? LastIgdbSyncAt { get; init; }
    public double? IgdbRating { get; init; }
    public int? IgdbRatingCount { get; init; }
}
