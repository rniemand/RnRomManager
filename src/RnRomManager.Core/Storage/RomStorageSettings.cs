using Microsoft.Extensions.Logging;
using RnRomManager.Core.Configuration;
using RnRomManager.Core.Library;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Storage;

/// <summary>
/// B2: <c>storage.embedRoms.&lt;systemId&gt;</c> = <c>true</c>/<c>false</c>. Takes the registry lazily because the
/// configurable handlers read their value from here. Z3: <c>storage.zipCompression.&lt;systemId&gt;</c> =
/// <c>Store</c>/<c>Med</c>/<c>High</c> for every registered system.
/// </summary>
public sealed class RomStorageSettings(ISettingsRepository settings, Func<ISystemRegistry> systems, ILogger<RomStorageSettings> logger)
    : IRomStorageSettings
{
    private volatile IReadOnlyDictionary<string, bool> _values = new Dictionary<string, bool>();
    private volatile IReadOnlyDictionary<string, ZipCompression> _compression = new Dictionary<string, ZipCompression>();

    public static string Key(string systemId) => $"storage.embedRoms.{systemId}";

    public static string CompressionKey(string systemId) => $"storage.zipCompression.{systemId}";

    /// <summary>Z3: an exact, case-sensitive match of a level name (unlike <c>Enum.TryParse</c>, which takes <c>med</c> or <c>2</c>).</summary>
    public static ZipCompression? ParseCompression(string? text) => text switch
    {
        nameof(ZipCompression.Store) => ZipCompression.Store,
        nameof(ZipCompression.Med) => ZipCompression.Med,
        nameof(ZipCompression.High) => ZipCompression.High,
        _ => null,
    };

    public ZipCompression Compression(string systemId) =>
        _compression.TryGetValue(systemId, out var level) ? level : ZipCompression.Med;

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

        var all = systems().All.Select(h => h.Id).ToList();
        var storedLevels = await settings.GetAsync(all.Select(CompressionKey).ToList(), cancellationToken);
        var levels = new Dictionary<string, ZipCompression>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in all)
        {
            if (!storedLevels.TryGetValue(CompressionKey(id), out var text))
                continue;
            if (ParseCompression(text) is { } level)
                levels[id] = level;
            else
                logger.LogWarning("Ignoring setting {Key} = {Value}: not Store, Med or High; Med applies", CompressionKey(id), text);
        }
        _compression = levels;
    }

    public async Task SetAsync(string systemId, bool embed, CancellationToken cancellationToken)
    {
        await settings.SetAsync(new Dictionary<string, string> { [Key(systemId)] = embed ? "true" : "false" }, cancellationToken);
        _values = new Dictionary<string, bool>(_values) { [systemId] = embed };
    }

    public async Task SetCompressionAsync(string systemId, ZipCompression level, CancellationToken cancellationToken)
    {
        await settings.SetAsync(new Dictionary<string, string> { [CompressionKey(systemId)] = level.ToString() }, cancellationToken);
        _compression = new Dictionary<string, ZipCompression>(_compression, StringComparer.OrdinalIgnoreCase) { [systemId] = level };
    }
}
