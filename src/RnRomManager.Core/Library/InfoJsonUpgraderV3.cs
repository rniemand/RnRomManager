using System.Text.Json.Nodes;

namespace RnRomManager.Core.Library;

/// <summary>
/// v3 → v4: every rom gets <c>externalFileName: null</c> (every system before v4 kept its ROMs inside the ZIP).
/// Only object elements of a <c>roms</c> array are touched; any other shape is left for the typed read and the
/// validator to report.
/// </summary>
public sealed class InfoJsonUpgraderV3 : IInfoJsonUpgrader
{
    public int FromVersion => 3;

    public void Upgrade(JsonObject document)
    {
        if (document["roms"] is not JsonArray roms)
            return; // the typed read reports the missing list
        foreach (var rom in roms.OfType<JsonObject>())
            rom["externalFileName"] = null;
    }
}
