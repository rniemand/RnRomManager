namespace RnRomManager.Core.Configuration;

/// <summary><c>app_settings</c> (B15): name → value pairs edited at runtime; a missing name means its default.</summary>
public interface ISettingsRepository
{
    /// <summary>The stored values of <paramref name="names"/>; names without a row are absent.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces every value in one transaction.</summary>
    Task SetAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);
}
