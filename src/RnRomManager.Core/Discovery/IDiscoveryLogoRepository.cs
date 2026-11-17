namespace RnRomManager.Core.Discovery;

/// <summary>
/// <c>discovery_logos</c> (B5, B13): one logo per kind and key. Keys are 64-character lowercase hexadecimal; callers validate them.
/// </summary>
public interface IDiscoveryLogoRepository
{
    /// <summary>The logo for <paramref name="key"/> of <paramref name="kind"/>, or null when there is none.</summary>
    Task<DiscoveryLogo?> GetAsync(LogoKind kind, string key, CancellationToken cancellationToken);

    /// <summary>Inserts or atomically replaces the logo for <see cref="DiscoveryLogo.Kind"/> and <see cref="DiscoveryLogo.Key"/>; the last writer wins.</summary>
    Task UpsertAsync(DiscoveryLogo logo, CancellationToken cancellationToken);

    /// <summary>Removes the logo; false when there was none.</summary>
    Task<bool> DeleteAsync(LogoKind kind, string key, CancellationToken cancellationToken);

    /// <summary><c>updated_at</c> of the logos of <paramref name="kind"/> among <paramref name="keys"/>, by key (B6 <c>logoUrl</c>).</summary>
    Task<IReadOnlyDictionary<string, DateTime>> ListUpdatedAtAsync(LogoKind kind, IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
}
