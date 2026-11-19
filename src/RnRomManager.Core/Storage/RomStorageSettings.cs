using Microsoft.Extensions.Logging;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Storage;

/// <summary>ROM Zip Config B2: the Admin's ROM storage choice per <see cref="ISystemHandler.RomStorageConfigurable"/> system.</summary>
public interface IRomStorageSettings
{
    /// <returns>The stored value, or null when none is stored (the handler default applies).</returns>
    bool? Override(string systemId);

    /// <summary>(Re)reads the keys of the configurable systems from <c>app_settings</c>, replacing every value held.</summary>
    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>Writes the database first, then the in-memory value. Only called under the job lock (B7).</summary>
    Task SetAsync(string systemId, bool embed, CancellationToken cancellationToken);
}

/// <summary>
/// B2: <c>storage.embedRoms.&lt;systemId&gt;</c> = <c>true</c>/<c>false</c>. Takes the registry lazily because the
/// configurable handlers read their value from here.
/// </summary>
public sealed class RomStorageSettings(ISettingsRepository settings, Func<ISystemRegistry> systems, ILogger<RomStorageSettings> logger)
    : IRomStorageSettings
{
    private volatile IReadOnlyDictionary<string, bool> _values = new Dictionary<string, bool>();

    public static string Key(string systemId) => $"storage.embedRoms.{systemId}";

    public bool? Override(string systemId) => _values.TryGetValue(systemId, out var embed) ? embed : null;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var ids = systems().All.Where(h => h.RomStorageConfigurable).Select(h => h.Id).ToList();
        var stored = await settings.GetAsync(ids.Select(Key).ToList(), cancellationToken);
        var values = new Dictionary<string, bool>();
        foreach (var id in ids)
        {
            if (!stored.TryGetValue(Key(id), out var text))
                continue;
            if (bool.TryParse(text, out var embed))
                values[id] = embed;
            else
                logger.LogWarning("Ignoring setting {Key} = {Value}: not true or false; the system's default ROM storage applies", Key(id), text);
        }
        _values = values;
    }

    public async Task SetAsync(string systemId, bool embed, CancellationToken cancellationToken)
    {
        await settings.SetAsync(new Dictionary<string, string> { [Key(systemId)] = embed ? "true" : "false" }, cancellationToken);
        _values = new Dictionary<string, bool>(_values) { [systemId] = embed };
    }
}
