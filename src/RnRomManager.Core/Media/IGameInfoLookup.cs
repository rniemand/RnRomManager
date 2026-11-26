namespace RnRomManager.Core.Media;

/// <summary>IGDB metadata lookup (L3): up to five games matching a title, for the Edit Metadata modal.</summary>
public interface IGameInfoLookup
{
    /// <summary>Both <c>Sources:Igdb*</c> keys are set.</summary>
    bool Configured { get; }

    /// <summary><paramref name="title"/> goes through <see cref="IgdbClient.SearchTitle"/>. Cancellation propagates.</summary>
    Task<GameInfoLookupResult> LookupAsync(string title, string systemId, CancellationToken cancellationToken);

    /// <summary>T7: the IGDB page of game <paramref name="igdbId"/>. Cancellation propagates.</summary>
    Task<GamePageResult> GamePageUrlAsync(int igdbId, CancellationToken cancellationToken);
}
