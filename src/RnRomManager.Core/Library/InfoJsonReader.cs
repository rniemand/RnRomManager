using System.Text.Json;
using System.Text.Json.Nodes;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Library;

/// <summary>
/// Reads <c>info.json</c> bytes (spec §6.2, §9.3): size limit → JSON → <c>schemaVersion</c> → upgrade chain
/// (vN→vN+1, in memory only) → typed model → <see cref="InfoJsonValidator"/>.
/// </summary>
public sealed class InfoJsonReader(IEnumerable<IInfoJsonUpgrader> upgraders, ISystemRegistry systems)
{
    public const int CurrentVersion = 3;
    public const int MaxBytes = 1024 * 1024;

    private readonly Dictionary<int, IInfoJsonUpgrader> _upgraders = upgraders.ToDictionary(u => u.FromVersion);

    public InfoJsonReadResult Read(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxBytes)
            return InfoJsonReadResult.Invalid($"info.json is larger than {MaxBytes} bytes");

        // We never write a BOM, but a hand-edited file may have one; it's still valid UTF-8.
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            utf8 = utf8[3..];

        JsonObject document;
        try
        {
            document = JsonNode.Parse(utf8) as JsonObject ?? throw new JsonException("info.json is not a JSON object.");
        }
        catch (JsonException ex)
        {
            return InfoJsonReadResult.Invalid($"not valid UTF-8 JSON: {ex.Message}");
        }

        if (document["schemaVersion"] is not JsonValue versionNode || !versionNode.TryGetValue<int>(out var fileVersion))
            return InfoJsonReadResult.Invalid("schemaVersion must be an integer");
        if (fileVersion > CurrentVersion)
            return InfoJsonReadResult.Newer(fileVersion);

        for (var version = fileVersion; version < CurrentVersion; version++)
        {
            if (!_upgraders.TryGetValue(version, out var upgrader))
                return InfoJsonReadResult.Invalid($"no upgrader from schema v{version}", fileVersion);
            upgrader.Upgrade(document);
            document["schemaVersion"] = version + 1;
        }

        InfoJsonGame game;
        try
        {
            game = document.Deserialize<InfoJsonGame>(InfoJsonSerializer.Options)
                ?? throw new JsonException("info.json is null.");
        }
        catch (JsonException ex)
        {
            return InfoJsonReadResult.Invalid($"missing or wrongly typed field: {ex.Message}", fileVersion);
        }

        var rule = InfoJsonValidator.Validate(game, systems.All.Select(h => h.Id).ToHashSet(StringComparer.Ordinal));
        return rule is null ? InfoJsonReadResult.Ok(game, fileVersion) : InfoJsonReadResult.Invalid(rule, fileVersion);
    }
}
