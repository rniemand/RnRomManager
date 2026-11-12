using System.Text.Json.Nodes;

namespace RnRomManager.Core.Library;

/// <summary>
/// v5 → v6 (B12): every rom gets <c>files: null</c> (a single-file release). Only object elements of a <c>roms</c>
/// array are touched; any other shape is left for the typed read and the validator to report.
/// </summary>
public sealed class InfoJsonUpgraderV5 : IInfoJsonUpgrader
{
    public int FromVersion => 5;

    public void Upgrade(JsonObject document)
    {
        if (document["roms"] is not JsonArray roms)
            return; // the typed read reports the missing list
        foreach (var rom in roms.OfType<JsonObject>())
            rom["files"] = null;
    }
}
