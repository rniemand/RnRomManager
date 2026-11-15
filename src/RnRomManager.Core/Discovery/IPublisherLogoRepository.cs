namespace RnRomManager.Core.Discovery;

/// <summary>
/// <c>publisher_logos</c> (B5). Keys are the 64-character lowercase hexadecimal group keys of B2; callers validate them.
/// </summary>
public interface IPublisherLogoRepository
{
    /// <summary>The override for <paramref name="key"/>, or null when there is none.</summary>
    Task<PublisherLogo?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Inserts or atomically replaces the override for <see cref="PublisherLogo.Key"/>; the last writer wins.</summary>
    Task UpsertAsync(PublisherLogo logo, CancellationToken cancellationToken);

    /// <summary>Removes the override; false when there was none.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);
}
