namespace RnRomManager.Core.Media;

/// <summary>Pull Cover Images and Refresh IGDB Ratings (P3, R2): batch reads by IGDB id and cover downloads.</summary>
public interface IIgdbGames
{
    /// <summary>Both <c>Sources:Igdb*</c> keys are set.</summary>
    bool Configured { get; }

    /// <summary>
    /// The games IGDB returns for <paramref name="ids"/>, queried in batches of <see cref="IgdbClient.MaxBatch"/>. A failure
    /// throws with one of the <see cref="IgdbClient"/> failure titles as its message; cancellation propagates.
    /// </summary>
    Task<IReadOnlyList<IgdbGameRating>> GetGamesAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken);

    /// <summary>The <c>t_1080p</c> cover <paramref name="imageId"/>. Never throws but for cancellation.</summary>
    Task<IgdbCoverDownload> DownloadCoverAsync(string imageId, CancellationToken cancellationToken);
}
