using System.Text.Json.Nodes;
using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Library;

/// <summary>
/// v4 → v5: every rom gets <c>version</c>: <see cref="ReleaseVersion.From"/> on its <c>originalFileName</c> when its
/// <c>flags</c> array holds <c>"Update"</c>, else null. Only object elements of a <c>roms</c> array are touched; any
/// other shape is left for the typed read and the validator to report.
/// </summary>
public sealed class InfoJsonUpgraderV4 : IInfoJsonUpgrader
{
    public int FromVersion => 4;

    public void Upgrade(JsonObject document)
    {
        if (document["roms"] is not JsonArray roms)
            return; // the typed read reports the missing list
        foreach (var rom in roms.OfType<JsonObject>())
        {
            var isUpdate = rom["flags"] is JsonArray flags && flags.Any(IsUpdateFlag);
            rom["version"] = isUpdate && rom["originalFileName"] is JsonValue name && name.TryGetValue<string>(out var text)
                ? ReleaseVersion.From(text)
                : null;
        }
    }

    private static bool IsUpdateFlag(JsonNode? flag) =>
        flag is JsonValue value && value.TryGetValue<string>(out var text) && text == ReleaseVersion.UpdateFlag;
}
