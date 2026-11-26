using RnRomManager.Core.Configuration;

namespace RnRomManager.Core.Tests.Backups;

internal sealed class BackupServiceInMemorySettings : ISettingsRepository
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(Values.Where(v => names.Contains(v.Key)).ToDictionary());

    public Task SetAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        foreach (var (name, value) in values)
            Values[name] = value;
        return Task.CompletedTask;
    }
}
