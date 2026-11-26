using System.Collections.Concurrent;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Api.Tests;

internal sealed class BackupSchedulerInMemorySettings : ISettingsRepository
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(_values.Where(v => names.Contains(v.Key)).ToDictionary());

    public Task SetAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        foreach (var (name, value) in values)
            _values[name] = value;
        return Task.CompletedTask;
    }
}
