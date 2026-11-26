namespace RnRomManager.Core.Library;

public interface IImageRepository
{
    Task<IReadOnlyList<ImageRow>> ListByGameAsync(Guid gameId, CancellationToken cancellationToken);

    /// <summary>null when the image doesn't exist or belongs to another game.</summary>
    Task<ImageRow?> GetAsync(Guid gameId, long id, CancellationToken cancellationToken);

    /// <summary>C5: every game's cover (its lowest-id <c>is_cover</c> image), with the game's ZIP.</summary>
    Task<IReadOnlyList<CoverRow>> ListCoversAsync(CancellationToken cancellationToken);
}
